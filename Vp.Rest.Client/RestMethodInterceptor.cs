﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Castle.Core.Internal;
using Castle.DynamicProxy;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Vp.Rest.Client.Builders;
using Vp.Rest.Client.Content;
using Vp.Rest.Client.Models;

namespace Vp.Rest.Client
{
    public class RestMethodInterceptor : IInterceptor
    {
        private readonly RestMethodOptions _options;

        private readonly IDictionary<RestMethod, HttpMethod> _httpMethodMap 
            = new Dictionary<RestMethod, HttpMethod>
        {
            {RestMethod.GET, HttpMethod.Get},
            {RestMethod.POST, HttpMethod.Post},
            {RestMethod.PUT, HttpMethod.Put},
            {RestMethod.DELETE, HttpMethod.Delete},
            {RestMethod.HEAD, HttpMethod.Head},
            {RestMethod.OPTION, HttpMethod.Options},
        };

        private readonly IDictionary<string, Lazy<IContentManager>> _contentProvider =
            new Dictionary<string, Lazy<IContentManager>>
            {
                { "application/json", new Lazy<IContentManager>(() => new JsonContentManager())},
                { "application/xml", new Lazy<IContentManager>(() => new XmlContentManger()) }
            };

        public RestMethodInterceptor(IOptions<RestMethodOptions> options)
        {
            _options = options.Value;
        }

        public void Intercept(IInvocation invocation)
        {
            var method = invocation.GetConcreteMethod();
            var client = HttpClientBuilder.Build(_options.Handlers);

            var restAttribute = method.GetAttribute<RestAttribute>();
            
            var httpMethod = _httpMethodMap[restAttribute.Method];

            var parametersInfo = method.GetParameters();
            var parameters = parametersInfo
                .Zip(invocation.Arguments, 
                    (paramInfo, value) 
                        => new Parameter(paramInfo, value));


            var relativeUrl = UriBuilder.Build(restAttribute.TemplatePath, parameters);

            var content = CreateHttpContent(parameters, restAttribute.ContetnType);

            var request = CreateHttpRequest(httpMethod, new Uri(relativeUrl, UriKind.Relative), content, null);
            Execute(invocation, client, request);

        }

        private HttpRequestMessage CreateHttpRequest( 
            HttpMethod method, 
            Uri relativeUrl, 
            HttpContent content,
            IEnumerable<KeyValuePair<string, string>> headers)
        {
            var request = new HttpRequestMessage(method, new Uri(new Uri(_options.Url, UriKind.Absolute), relativeUrl));
            
            foreach (var header in headers ?? Enumerable.Empty<KeyValuePair<string, string>>())
            {
                request.Headers.Add(header.Key, header.Value);
            }

            if (method != HttpMethod.Get)
            {
                request.Content = content;
            }

            return request;
        }

        private void Execute(IInvocation invocation, HttpClient client, HttpRequestMessage requestMessage)
        {
            var restMethodInfo = invocation.GetConcreteMethod();
            Task<HttpResponseMessage> task = null;
            if (restMethodInfo.ReturnType == typeof(Task))
            {
                task = client.SendAsync(requestMessage);
                invocation.ReturnValue = task;
            }

            else if(typeof(Task).IsAssignableFrom(restMethodInfo.ReturnType))
            {
                task = client.SendAsync(requestMessage);
                var unwrapType = restMethodInfo.ReturnType.GetGenericArguments()[0];
                var completion = ReflectionHelper.CreateCompletionTaskSourceForType(unwrapType);
                task.ContinueWith(currentTask =>
                {
                    if (currentTask.IsFaulted)
                    {
                        completion.SetException(currentTask.Exception);
                    }

                    if (currentTask.Status == TaskStatus.RanToCompletion)
                    {
                        var content = currentTask.Result.Content;
                        var contentManger = _contentProvider[content.Headers.ContentType.MediaType];
                        var responseTask = contentManger.Value.ReadContent(content, unwrapType);
                        responseTask.ContinueWith(readTask =>
                        {
                            if (readTask.IsFaulted)
                            {
                                completion.SetException(readTask.Exception);
                            }

                            completion.SetResult(readTask.Result);
                        });
                    }
                });
                
                invocation.ReturnValue = completion.Task;
            }
           
        }

        private HttpContent CreateHttpContent(IEnumerable<Parameter> parameters, string contentType)
        {
            var bodyParameter = parameters
                .FirstOrDefault(p => p.ParameterInfo.GetCustomAttribute<BodyAttribute>() != null);
            
            if(bodyParameter == null)
                return null;

            var contentManager = _contentProvider[contentType];

            return contentManager.Value.CreateContent(bodyParameter.Value);
        }

       
        
    }
}