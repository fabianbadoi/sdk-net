﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Authentication;
using System.Web.Helpers;
using Penneo.Util;
using RestSharp;

namespace Penneo.Connector
{
    internal class ApiConnector : IApiConnector
    {
        /// <summary>
        /// The Penneo server endpoint
        /// </summary>
        private static string _endpoint = "https://sandbox.penneo.com/api/v1";

        /// <summary>
        /// The api connector factory
        /// </summary>
        private static Func<IApiConnector> _factory;

        /// <summary>
        /// The singleton instance
        /// </summary>
        private static IApiConnector _instance;

        /// <summary>
        /// Success status codes
        /// </summary>
        private readonly List<HttpStatusCode> _successStatusCodes = new List<HttpStatusCode> {HttpStatusCode.OK, HttpStatusCode.Created, HttpStatusCode.NoContent};

        /// <summary>
        /// The rest http client
        /// </summary>
        private RestClient _client;

        /// <summary>
        /// Rest resources
        /// </summary>
        private RestResources _restResources;

        /// <summary>
        /// Http headers
        /// </summary>
        private Dictionary<string, string> _headers;

        protected ApiConnector()
        {
            Init();
        }

        /// <summary>
        /// Singleton instance
        /// </summary>
        public static IApiConnector Instance
        {
            get
            {
                if (_instance != null)
                {
                    return _instance;
                }

                if (!PenneoConnector.IsInitialized)
                {
                    throw new AuthenticationException("The Penneo connector has not been initialized");
                }

                if (_factory != null)
                {
                    _instance = _factory();
                }
                else
                {
                    _instance = new ApiConnector();
                }
                return _instance;
            }
        }

        #region IApiConnector Members

        /// <summary>
        /// <see cref="IApiConnector.WriteObject"/>
        /// </summary>
        public bool WriteObject(Entity obj)
        {
            var data = obj.GetRequestData();
            if (data == null)
            {
                return false;
            }
            if (!obj.IsNew)
            {
                var response = CallServer(obj.RelativeUrl + "/" + obj.Id, data, Method.PUT);
                if (response == null || !_successStatusCodes.Contains(response.StatusCode))
                {
                    return false;
                }
            }
            else
            {
                var response = CallServer(obj.RelativeUrl, data, Method.POST);
                if (response == null || !_successStatusCodes.Contains(response.StatusCode))
                {
                    return false;
                }

                //Update object with values returned from the API for the created object
                ReflectionUtil.SetPropertiesFromJson(obj, response.Content);
            }
            return true;
        }

        /// <summary>
        /// <see cref="IApiConnector.DeleteObject"/>
        /// </summary>
        public bool DeleteObject(Entity obj)
        {
            var response = CallServer(obj.RelativeUrl + '/' + obj.Id, null, Method.DELETE);
            return response != null && (response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.NoContent);
        }

        /// <summary>
        /// <see cref="IApiConnector.ReadObject"/>
        /// </summary>
        public bool ReadObject(Entity obj)
        {
            var response = CallServer(obj.RelativeUrl + '/' + obj.Id);
            if (response == null || response.StatusCode != HttpStatusCode.OK)
            {
                return false;
            }
            ReflectionUtil.SetPropertiesFromJson(obj, response.Content);
            return true;
        }

        /// <summary>
        /// <see cref="IApiConnector.LinkEntity"/>
        /// </summary>
        public bool LinkEntity(Entity parent, Entity child)
        {
            var url = parent.RelativeUrl + "/" + parent.Id + "/" + _restResources.GetResource(child.GetType()) + "/" + child.Id;
            var response = CallServer(url, customMethod: "LINK");

            if (response == null || !_successStatusCodes.Contains(response.StatusCode))
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// <see cref="IApiConnector.GetLinkedEntities{T}"/>
        /// </summary>
        public IEnumerable<T> GetLinkedEntities<T>(Entity obj)
        {
            var url = obj.RelativeUrl + "/" + obj.Id + "/" + _restResources.GetResource<T>();
            var response = CallServer(url);
            return CreateObjects<T>(response.Content);
        }

        /// <summary>
        /// <see cref="IApiConnector.FindLinkedEntity{T}"/>
        /// </summary>
        public T FindLinkedEntity<T>(Entity obj, int id)
        {
            var url = obj.RelativeUrl + "/" + obj.Id + "/" + _restResources.GetResource<T>() + "/" + id;
            var response = CallServer(url);
            if (response == null || !_successStatusCodes.Contains(response.StatusCode))
            {
                throw new Exception("Penneo: Internal problem encountered");
            }
            return CreateObject<T>(response.Content);
        }

        /// <summary>
        /// <see cref="IApiConnector.GetFileAssets"/>
        /// </summary>
        public byte[] GetFileAssets(Entity obj, string assetName)
        {
            var encoded = GetTextAssets(obj, assetName);
            return Convert.FromBase64String(encoded);
        }

        /// <summary>
        /// <see cref="IApiConnector.GetTextAssets"/>
        /// </summary>
        public string GetTextAssets(Entity obj, string assetName)
        {
            var url = obj.RelativeUrl + "/" + obj.Id + "/" + assetName;
            var response = CallServer(url);
            var text = Json.Decode(response.Content)[0];
            return text;
        }

        /// <summary>
        /// <see cref="IApiConnector.FindBy{T}"/>
        /// </summary>
        public bool FindBy<T>(Dictionary<string, object> query, out IEnumerable<T> objects)
            where T : Entity
        {
            var resource = _restResources.GetResource<T>();

            Dictionary<string, Dictionary<string, object>> options = null;
            if (query != null && query.Count > 0)
            {
                options = new Dictionary<string, Dictionary<string, object>>();
                options.Add("query", query);
            }
            var response = CallServer(resource, null, Method.GET, options);
            if (response == null || !_successStatusCodes.Contains(response.StatusCode))
            {
                objects = null;
                return false;
            }

            objects = CreateObjects<T>(response.Content);
            return true;
        }

        /// <summary>
        /// <see cref="IApiConnector.PerformAction"/>
        /// </summary>
        public bool PerformAction(Entity obj, string actionName)
        {
            var url = obj.RelativeUrl + "/" + obj.Id + "/" + actionName;
            var response = CallServer(url, customMethod: "patch");
            if (response == null || !_successStatusCodes.Contains(response.StatusCode))
            {
                return false;
            }
            return true;
        }

        #endregion

        /// <summary>
        /// Initializes the API connector with rest client, endpoint, headers and authentication
        /// </summary>
        private void Init()
        {
            if (!string.IsNullOrEmpty(PenneoConnector.Endpoint))
            {
                _endpoint = PenneoConnector.Endpoint;
            }
            _client = new RestClient(_endpoint);

            _restResources = ServiceLocator.Instance.GetInstance<RestResources>();

            _headers = PenneoConnector.Headers ?? new Dictionary<string, string>();
            _headers.Add("Content-type", "application/json");

            if (!string.IsNullOrEmpty(PenneoConnector.User))
            {
                _headers.Add("penneo-api-user", PenneoConnector.User);
            }

            _client.Authenticator = new WSSEAuthenticator(PenneoConnector.Key, PenneoConnector.Secret);
            PenneoConnector.Reset();
        }

        /// <summary>
        /// Sets a factory for creating a connector.
        /// </summary>
        public static void SetFactory(Func<IApiConnector> factory)
        {
            _factory = factory;

            //Null instance if a new factory is provided
            _instance = null;
        }

        /// <summary>
        /// Prepare a rest request
        /// </summary>
        private RestRequest PrepareRequest(string url, Dictionary<string, object> data = null, Method method = Method.GET, Dictionary<string, Dictionary<string, object>> options = null)
        {
            var request = new RestRequest(url, method);
            foreach (var h in _headers)
            {
                request.AddHeader(h.Key, h.Value);
            }

            if (options != null)
            {
                foreach (var entry in options)
                {
                    var key = entry.Key;
                    var o = entry.Value;
                    if (key == "query")
                    {
                        VisitQuery(request, o);
                    }
                }
            }

            if (data != null)
            {
                request.RequestFormat = DataFormat.Json;
                request.AddBody(data);
            }
            return request;
        }

        /// <summary>
        /// Parse 'query' options into request data
        /// </summary>
        private static void VisitQuery(RestRequest request, Dictionary<string, object> query)
        {
            foreach (var entry in query)
            {
                request.AddParameter(StringUtil.FirstCharacterToLower(entry.Key), entry.Value);
            }
        }

        /// <summary>
        /// Calls the Penneo server with a rest request
        /// </summary>
        public IRestResponse CallServer(string url, Dictionary<string, object> data = null, Method method = Method.GET, Dictionary<string, Dictionary<string, object>> options = null, string customMethod = null)
        {
            try
            {
                var request = PrepareRequest(url, data, method, options);
                IRestResponse response;

                string actualMethod;
                if (string.IsNullOrEmpty(customMethod))
                {
                    actualMethod = method.ToString();
                    response = _client.Execute(request);
                }
                else
                {
                    actualMethod = customMethod;
                    response = _client.ExecuteAsGet(request, customMethod);
                }
                Log.Write("Request " + actualMethod + " " + url + " /  Response '" + response.StatusCode + "'", LogSeverity.Trace);
                return response;
            }
            catch (Exception ex)
            {
                Log.Write(ex.ToString(), LogSeverity.Fatal);
                return null;
            }
        }

        /// <summary>
        /// Create objects from a json string
        /// </summary>
        private static IEnumerable<T> CreateObjects<T>(string json)
        {
            var result = new List<T>();
            var values = Json.Decode<List<Dictionary<string, object>>>(json);
            foreach (var v in values)
            {
                var instance = Activator.CreateInstance<T>();
                ReflectionUtil.SetPropertiesFromDictionary(instance, v);
                result.Add(instance);
            }
            return result;
        }

        /// <summary>
        /// Creates a single object from a json string
        /// </summary>
        private static T CreateObject<T>(string json)
        {
            var instance = Activator.CreateInstance<T>();
            var values = Json.Decode<Dictionary<string, object>>(json);
            ReflectionUtil.SetPropertiesFromDictionary(instance, values);
            return instance;
        }
    }
}