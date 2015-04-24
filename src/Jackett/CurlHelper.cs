﻿using CurlSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Net.Http.Headers;

namespace Jackett
{
    public class CurlHelper
    {
        private const string ChromeUserAgent = "Mozilla/5.0 (Windows NT 6.3; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/42.0.2311.90 Safari/537.36";

        public static CurlHelper Shared = new CurlHelper();

        private BlockingCollection<CurlRequest> curlRequests = new BlockingCollection<CurlRequest>();

        public bool IsSupported { get; private set; }

        private class CurlRequest
        {
            public TaskCompletionSource<CurlResponse> TaskCompletion { get; private set; }

            public string Url { get; private set; }

            public string Cookies { get; private set; }

            public string Referer { get; private set; }

            public HttpMethod Method { get; private set; }

            public Dictionary<string, string> PostData { get; set; }

            public CurlRequest(HttpMethod method, string url, string cookies = null, string referer = null)
            {
                TaskCompletion = new TaskCompletionSource<CurlResponse>();
                Method = method;
                Url = url;
                Cookies = cookies;
                Referer = referer;
            }
        }

        public class CurlResponse
        {
            public Dictionary<string, string> Headers { get; private set; }

            public List<string[]> HeaderList { get; private set; }

            public byte[] Content { get; private set; }

            public Dictionary<string, string> Cookies { get; private set; }

            public List<string> CookiesFlat { get { return Cookies.Select(c => c.Key + "=" + c.Value).ToList(); } }

            public string CookieHeader { get { return string.Join("; ", CookiesFlat); } }

            public CurlResponse(List<string[]> headers, byte[] content)
            {
                Headers = new Dictionary<string, string>();
                Cookies = new Dictionary<string, string>();
                HeaderList = headers;
                Content = content;
                foreach (var h in headers)
                {
                    Headers[h[0]] = h[1];
                }
            }

            public void AddCookiesFromHeaders(List<string[]> headers)
            {
                foreach (var h in headers)
                {
                    if (h[0] == "set-cookie")
                    {
                        var rawCookies = h[1].Split(';');
                        foreach (var rawCookie in rawCookies)
                        {
                            var parts = rawCookie.Split(new char[] { '=' }, 2, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length == 1)
                                Cookies[rawCookie.Trim()] = string.Empty;
                            else
                                Cookies[parts[0].Trim()] = parts[1].Trim();
                        }
                    }
                }
            }
        }

        public async Task<CurlResponse> GetAsync(string url, string cookies = null, string referer = null)
        {
            var curlRequest = new CurlRequest(HttpMethod.Get, url, cookies, referer);
            curlRequests.Add(curlRequest);
            var result = await curlRequest.TaskCompletion.Task;
            var checkedResult = await FollowRedirect(url, result);
            return checkedResult;
        }

        public async Task<CurlResponse> PostAsync(string url, Dictionary<string, string> formData, string cookies = null, string referer = null)
        {
            var curlRequest = new CurlRequest(HttpMethod.Post, url, cookies, referer);
            curlRequest.PostData = formData;
            curlRequests.Add(curlRequest);
            var result = await curlRequest.TaskCompletion.Task;
            var checkedResult = await FollowRedirect(url, result);
            return checkedResult;
        }

        private async Task<CurlResponse> FollowRedirect(string url, CurlResponse response)
        {
            var uri = new Uri(url);
            string redirect;
            if (response.Headers.TryGetValue("location", out redirect))
            {
                string cookie = response.Headers["set-cookie"];
                var redirectUrl = uri.Scheme + "://" + uri.Host + "/" + redirect;
                var newRedirect = await GetAsync(redirectUrl, cookie, url);
                newRedirect.AddCookiesFromHeaders(response.HeaderList);
                return newRedirect;
            }
            else
                return response;
        }

        public CurlHelper()
        {
            try
            {
                Curl.GlobalInit(CurlInitFlag.All);
                IsSupported = true;
            }
            catch (Exception ex)
            {
                IsSupported = false;
            }
            Task.Run((Action)CurlServicer);
        }

        private void CurlServicer()
        {
            foreach (var curlRequest in curlRequests.GetConsumingEnumerable())
            {
                PerformCurl(curlRequest);
            }
        }

        private void PerformCurl(CurlRequest curlRequest)
        {
            var headerBuffers = new List<byte[]>();
            var contentBuffers = new List<byte[]>();
            try
            {
                using (var easy = new CurlEasy())
                {
                    easy.Url = curlRequest.Url;
                    easy.BufferSize = 64 * 1024;
                    easy.UserAgent = ChromeUserAgent;
                    easy.WriteFunction = (byte[] buf, int size, int nmemb, object data) =>
                    {
                        contentBuffers.Add(buf);
                        return size * nmemb;
                    };
                    easy.HeaderFunction = (byte[] buf, int size, int nmemb, object extraData) =>
                    {
                        headerBuffers.Add(buf);
                        return size * nmemb;
                    };

                    if (!string.IsNullOrEmpty(curlRequest.Cookies))
                        easy.Cookie = curlRequest.Cookies;

                    if (curlRequest.Method == HttpMethod.Post)
                    {
                        easy.Post = true;
                        var postString = new FormUrlEncodedContent(curlRequest.PostData).ReadAsStringAsync().Result;
                        easy.PostFields = postString;
                        easy.PostFieldSize = Encoding.UTF8.GetByteCount(postString);
                    }

                    easy.Perform();
                }

                var headerBytes = Combine(headerBuffers.ToArray());
                var headerString = Encoding.UTF8.GetString(headerBytes);
                var headerParts = headerString.Split(new char[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                var headers = new List<string[]>();
                foreach (var headerPart in headerParts.Skip(1))
                {
                    var keyVal = headerPart.Split(new char[] { ':' }, 2);
                    if (keyVal.Length > 1)
                    {
                        headers.Add(new[] { keyVal[0].ToLower().Trim(), keyVal[1].Trim() });
                    }
                }

                var contentBytes = Combine(contentBuffers.ToArray());
                //var contentString = Encoding.UTF8.GetString (contentBytes);

                var curlResponse = new CurlResponse(headers, contentBytes);
                curlResponse.AddCookiesFromHeaders(headers);
                curlRequest.TaskCompletion.SetResult(curlResponse);
            }
            catch (Exception ex)
            {
                curlRequest.TaskCompletion.TrySetException(ex);
            }
        }

        public static byte[] Combine(params byte[][] arrays)
        {
            byte[] ret = new byte[arrays.Sum(x => x.Length)];
            int offset = 0;
            foreach (byte[] data in arrays)
            {
                Buffer.BlockCopy(data, 0, ret, offset, data.Length);
                offset += data.Length;
            }
            return ret;
        }
    }
}