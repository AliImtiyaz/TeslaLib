﻿// Helper library to authenticate to Tesla Owner API 
// Includes support for MFA.

// This code is heavily based on Christian P (https://github.com/bassmaster187)'s
// work in the TeslaLogger tool (https://github.com/bassmaster187/TeslaLogger).
// My changes were largely to make it reusable.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using Newtonsoft.Json.Linq;

namespace TeslaAuth
{
    public sealed class MFACodeRequiredEventArgs : EventArgs
    {
        public String Username { get; set; }
    }

    public sealed class MFACodeInvalidEventArgs : EventArgs
    {
        public String Username { get; set; }
        public String Message { get; set; }
    }

    public static class TeslaAuthHelper
    {
        private const string TESLA_CLIENT_ID = "81527cff06843c8634fdc09e8ac0abefb46ac849f38fe1e431c2ef2106796384";
        private const string TESLA_CLIENT_SECRET = "c7257eb71a564034f9419ee651c7d0e5f7aa6bfbd18bafb5c5c033b093bb2fa3";
        private const string UserAgent = ".NET TeslaAuthHelper";
        private static readonly Random random = new Random();

        public static string RandomString(int length)
        {
            const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
            lock (random)
            {
                return new string(Enumerable.Repeat(chars, length)
                  .Select(s => s[random.Next(s.Length)]).ToArray());
            }
        }

        public static string ComputeSHA256Hash(string text)
        {
            string hashString;
            using (var sha256 = SHA256Managed.Create())
            {
                var hash = sha256.ComputeHash(Encoding.Default.GetBytes(text));
                hashString = ToHex(hash, false);
            }

            return hashString;
        }

        private static string ToHex(byte[] bytes, bool upperCase)
        {
            StringBuilder result = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
                result.Append(bytes[i].ToString(upperCase ? "X2" : "x2"));
            return result.ToString();
        }

        public static event EventHandler<MFACodeRequiredEventArgs> MFACodeRequired;

        public static event EventHandler<MFACodeInvalidEventArgs> MFACodeInvalid;

        public static async Task<Tokens> AuthenticateAsync(string username, string password, string mfaCode = null, TeslaAccountRegion region = TeslaAccountRegion.Unknown)
        {
            var loginInfo = await InitializeLoginAsync(region, username);
            loginInfo.UserName = username;
            var code = await GetAuthorizationCodeAsync(username, password, mfaCode, loginInfo, region);
            var tokens = await ExchangeCodeForBearerTokenAsync(code, loginInfo, region);
            var accessAndRefreshTokens = await ExchangeAccessTokenForBearerTokenAsync(tokens.AccessToken);
            return new Tokens {
                AccessToken = accessAndRefreshTokens.AccessToken,
                RefreshToken = tokens.RefreshToken,
                CreatedAt = accessAndRefreshTokens.CreatedAt,
                ExpiresIn = accessAndRefreshTokens.ExpiresIn
            };
        }


        /// <summary>
        /// Start the login process.  Tesla's logins are divided into regions based on country-specific data governance rules.
        /// </summary>
        /// <param name="region">Which region do we think the account is registered with?</param>
        /// <param name="emailAddress">Provide the email address and Tesla may redirect us to the right region.</param>
        /// <returns></returns>
        private static async Task<LoginInfo> InitializeLoginAsync(TeslaAccountRegion region = TeslaAccountRegion.Unknown, 
            string username = null) 
        {
            var result = new LoginInfo();

            result.CodeVerifier = RandomString(86);

            var code_challenge_SHA256 = ComputeSHA256Hash(result.CodeVerifier);
            result.CodeChallenge = Convert.ToBase64String(Encoding.Default.GetBytes(code_challenge_SHA256)); 

            result.State = RandomString(20);
            
            using (HttpClient client = new HttpClient())
            {
                UriBuilder b = new UriBuilder(GetBaseAddressForRegion(region) + "/oauth2/v3/authorize");
                b.Port = -1;
                var q = HttpUtility.ParseQueryString(b.Query);
                q.Add("client_id", "ownerapi");
                q.Add("code_challenge", result.CodeChallenge);
                q.Add("code_challenge_method", "S256");
                q.Add("redirect_uri", "https://auth.tesla.com/void/callback");
                q.Add("response_type", "code");
                q.Add("scope", "openid email offline_access");
                q.Add("state", result.State);
                if (username != null)
                {
                    q.Add("login_hint", username);
                }
                b.Query = q.ToString();
                string url = b.ToString();

                client.DefaultRequestHeaders.Add("User-agent", UserAgent);

                HttpResponseMessage response = await client.GetAsync(url);
                var resultContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == HttpStatusCode.SeeOther)
                    {
                        // @TODO: Handle redirect logic.  Test by say our US address is in China, and see what happens.
                        throw new NotImplementedException("TODO: Wrong Tesla region.  Need to look elsewhere!");
                    }

                    throw new Exception(String.Format("Initializing a Tesla login failed. {0}", response.ReasonPhrase));
                }

                var hiddenFields = Regex.Matches(resultContent, "type=\\\"hidden\\\" name=\\\"(.*?)\\\" value=\\\"(.*?)\\\"");
                var formFields = new Dictionary<string, string>();
                foreach (Match match in hiddenFields)
                {
                    formFields.Add(match.Groups[1].Value, match.Groups[2].Value);
                }

                IEnumerable<string> cookies = response.Headers.SingleOrDefault(header => header.Key.ToLowerInvariant() == "set-cookie").Value;
                var cookie = cookies.First();
                cookie = cookie.Substring(0, cookie.IndexOf(" "));
                cookie = cookie.Trim();

                result.Cookie = cookie;
                result.FormFields = formFields;
                
                return result;
            }            
        }

        private static async Task<string> GetAuthorizationCodeAsync(string username, string password, string mfaCode, LoginInfo loginInfo, TeslaAccountRegion region = TeslaAccountRegion.Unknown)
        {
            var formFields = loginInfo.FormFields;
            formFields.Add("identity", username);
            formFields.Add("credential", password);

            using (HttpClientHandler ch = new HttpClientHandler())
            {
                ch.AllowAutoRedirect = false;
                ch.UseCookies = false;
                using (HttpClient client = new HttpClient(ch))
                {
                    // client.Timeout = TimeSpan.FromSeconds(10);
                    client.BaseAddress = new Uri(GetBaseAddressForRegion(region));
                    client.DefaultRequestHeaders.Add("Cookie", loginInfo.Cookie);
                    client.DefaultRequestHeaders.Add("User-agent", UserAgent);
                    //DateTime start = DateTime.UtcNow;

                    using (FormUrlEncodedContent content = new FormUrlEncodedContent(formFields))
                    {
                        UriBuilder b = new UriBuilder(client.BaseAddress + "oauth2/v3/authorize");
                        b.Port = -1;
                        var q = HttpUtility.ParseQueryString(b.Query);
                        q["client_id"] = "ownerapi";
                        q["code_challenge"] = loginInfo.CodeChallenge;
                        q["code_challenge_method"] = "S256";
                        q["redirect_uri"] = "https://auth.tesla.com/void/callback";
                        q["response_type"] = "code";
                        q["scope"] = "openid email offline_access";
                        q["state"] = loginInfo.State;
                        b.Query = q.ToString();
                        string url = b.ToString();

                        //var temp = content.ReadAsStringAsync().Result;

                        HttpResponseMessage result = await client.PostAsync(url, content);
                        string resultContent = await result.Content.ReadAsStringAsync();

                        if (result.StatusCode != HttpStatusCode.Redirect && !result.IsSuccessStatusCode)
                        {
                            if (result.StatusCode == HttpStatusCode.Unauthorized)
                            {
                                throw new SecurityException($"Logging in failed for Tesla account {username}: {result.ReasonPhrase}.  Is your password correct?  Does your Tesla account allow mobile access?");
                            }

                            throw new Exception(String.IsNullOrEmpty(result.ReasonPhrase) ? result.StatusCode.ToString() : result.ReasonPhrase);
                        }
                        Uri location = result.Headers.Location;

                        
                        if (result.StatusCode != HttpStatusCode.Redirect)
                        {
                            if (result.StatusCode == HttpStatusCode.OK && resultContent.Contains("passcode"))
                            {
                                if (String.IsNullOrEmpty(mfaCode))
                                {
                                    OnMFACodeRequired(username);
                                }
                                return await GetAuthorizationCodeWithMfaAsync(mfaCode, loginInfo, region);

                            }
                            else if (result.StatusCode != HttpStatusCode.OK)
                            {
                                throw new Exception("Expected redirect did not occur.  Status code: " + result.StatusCode);
                            }
                        }

                        if (location == null)
                        {
                            throw new SecurityException($"Logging in failed for {username}.  The account may be locked.");
                        }

                        string code = HttpUtility.ParseQueryString(location.Query).Get("code");
                        return code;                       
                    }
                }
            }
            throw new Exception("Authentication process failed");
        }

        private static void OnMFACodeRequired(string username)
        {
            var localMFACodeRequired = MFACodeRequired;
            if (localMFACodeRequired != null)
            {
                var args = new MFACodeRequiredEventArgs();
                args.Username = username;
                localMFACodeRequired.Invoke(null, args);
            }
            else
                throw new Exception("Multi-factor code required to authenticate Tesla user " + username);
        }

        private static void OnMFACodeInvalid(string username, string message)
        {
            var localMFACodeInvalid = MFACodeInvalid;
            if (localMFACodeInvalid != null)
            {
                MFACodeInvalidEventArgs args = new MFACodeInvalidEventArgs();
                args.Username = username;
                args.Message = message;
                localMFACodeInvalid.Invoke(null, args);
            }
            else
                throw new Exception(String.Format("Multi-factor authentication code was invalid for Tesla user {0}: {1}",
                    username, message));
        }

        private static async Task<Tokens> ExchangeCodeForBearerTokenAsync(string code, LoginInfo loginInfo, TeslaAccountRegion region/* = TeslaAccountRegion.Unknown*/)
        {
            var body = new JObject();
            body.Add("grant_type", "authorization_code");
            body.Add("client_id", "ownerapi");
            body.Add("code", code );
            body.Add("code_verifier", loginInfo.CodeVerifier);
            body.Add("redirect_uri", "https://auth.tesla.com/void/callback");

            HttpClientHandler handler = new HttpClientHandler()
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            using (HttpClient client = new HttpClient(handler))
            {
                client.BaseAddress = new Uri(GetBaseAddressForRegion(region));
                client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
                client.DefaultRequestHeaders.Connection.Add("keep-alive");

                using (var content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), System.Text.Encoding.UTF8, "application/json"))
                {
                    HttpResponseMessage result = await client.PostAsync(client.BaseAddress + "oauth2/v3/token", content);
                    string resultContent = await result.Content.ReadAsStringAsync();
                    if (!result.IsSuccessStatusCode)
                    {
                        ThrowException(result, resultContent);
                    }

                    JObject response = JObject.Parse(resultContent);
                    
                    var tokens = new Tokens()
                    {
                        AccessToken = response["access_token"].Value<string>(),
                        RefreshToken = response["refresh_token"].Value<string>()
                    };
                    return tokens;
                }
            }  
        }

        private static async Task<Tokens> ExchangeAccessTokenForBearerTokenAsync(string accessToken)
        {   
            var body = new JObject();
            body.Add("grant_type", "urn:ietf:params:oauth:grant-type:jwt-bearer");
            body.Add("client_id", TESLA_CLIENT_ID);
            body.Add("client_secret", TESLA_CLIENT_SECRET);

            using (HttpClient client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(5);
                client.DefaultRequestHeaders.Add("Authorization", "Bearer " + accessToken);
                client.DefaultRequestHeaders.Add("User-agent", UserAgent);

                using (var content = new StringContent(body.ToString(), System.Text.Encoding.UTF8, "application/json"))
                {
                    HttpResponseMessage result = await client.PostAsync("https://owner-api.teslamotors.com/oauth/token", content);
                    string resultContent = await result.Content.ReadAsStringAsync();
                    if (!result.IsSuccessStatusCode)
                    {
                        ThrowException(result, resultContent);
                    }

                    JObject response = JObject.Parse(resultContent);
                    DateTime createdAt = ToDateTime(response["created_at"].Value<Int64>());
                    TimeSpan expiresIn = FromUnixTimeSpan(response["expires_in"].Value<Int64>());
                    var bearerToken = response["access_token"].Value<String>();
                    var refreshToken = response["refresh_token"].Value<String>();

                    return new Tokens {
                        AccessToken = bearerToken,
                        RefreshToken = refreshToken,
                        CreatedAt = createdAt,
                        ExpiresIn = expiresIn
                    };
                }
            }
        }

        public static async Task<Tokens> RefreshTokenAsync(string refreshToken, TeslaAccountRegion region = TeslaAccountRegion.Unknown)
        {
            var body = new JObject();
            body.Add("grant_type", "refresh_token");
            body.Add("client_id", "ownerapi");
            body.Add("refresh_token", refreshToken);
            body.Add("scope", "openid email offline_access");

            var handler = new HttpClientHandler()
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            using (HttpClient client = new HttpClient(handler))
            {
                client.Timeout = TimeSpan.FromSeconds(5);
                client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
                client.DefaultRequestHeaders.Connection.Add("keep-alive");
                client.DefaultRequestHeaders.Add("User-agent", UserAgent);

                using (var content = new StringContent(body.ToString(), System.Text.Encoding.UTF8, "application/json"))
                {
                    HttpResponseMessage result = await client.PostAsync(GetBaseAddressForRegion(region) + "/oauth2/v3/token", content);
                    string resultContent = await result.Content.ReadAsStringAsync();
                    if (!result.IsSuccessStatusCode)
                    {
                        ThrowException(result, resultContent);
                    }

                    JObject response = JObject.Parse(resultContent);
                    
                    string accessToken = response["access_token"].Value<String>();
                    return await ExchangeAccessTokenForBearerTokenAsync(accessToken);
                }
            }
        }

        private static void ThrowException(HttpResponseMessage result, string resultContent)
        {
            var ex = new Exception(String.Format("TeslaAuthHelper RefreshTokenAsync failed.  Status: {0}  Reason: {1}", result.StatusCode, result.ReasonPhrase));
            ex.Data["SerializedResponse"] = resultContent;
            ex.Data["StatusCode"] = result.StatusCode;
            throw ex;
        }

        private static async Task<string> GetAuthorizationCodeWithMfaAsync(string mfaCode, LoginInfo loginInfo, TeslaAccountRegion region = TeslaAccountRegion.Unknown)
        {
            string mfaFactorId = await GetMfaFactorIdAsync(loginInfo, region);
            await VerifyMfaCodeAsync(mfaCode, loginInfo, mfaFactorId, region);
            var code = await GetCodeAfterValidMfaAsync(loginInfo, region);
            return code;
        }

        private static async Task<string> GetMfaFactorIdAsync(LoginInfo loginInfo, TeslaAccountRegion region = TeslaAccountRegion.Unknown)
        {
            string resultContent;
            using (HttpClientHandler ch = new HttpClientHandler())
            {
                ch.UseCookies = false;
                using (HttpClient client = new HttpClient(ch))
                {
                    client.DefaultRequestHeaders.Add("Cookie", loginInfo.Cookie);
                    client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

                    UriBuilder b = new UriBuilder(GetBaseAddressForRegion(region) + "/oauth2/v3/authorize/mfa/factors");
                    b.Port = -1;

                    var q = HttpUtility.ParseQueryString(b.Query);
                    q.Add("transaction_id", loginInfo.FormFields["transaction_id"]);
                    b.Query = q.ToString();
                    string url = b.ToString();

                    HttpResponseMessage result = await client.GetAsync(url);
                    resultContent = await result.Content.ReadAsStringAsync();
                    if (!result.IsSuccessStatusCode)
                    {
                        ThrowException(result, resultContent);
                    }

                    var response = JObject.Parse(resultContent);
  
                    return response["data"][0]["id"].Value<string>();
                }
            }
        }

        private static async Task VerifyMfaCodeAsync(string mfaCode, LoginInfo loginInfo, string factorId, TeslaAccountRegion region = TeslaAccountRegion.Unknown)
        {
            using (HttpClientHandler ch = new HttpClientHandler())
            {
                ch.AllowAutoRedirect = false;
                ch.UseCookies = false;
                using (HttpClient client = new HttpClient(ch))
                {
                    client.BaseAddress = new Uri(GetBaseAddressForRegion(region));
                    client.DefaultRequestHeaders.Add("Cookie", loginInfo.Cookie);
                    client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
                    client.DefaultRequestHeaders.Referrer = new Uri("https://auth.tesla.com");

                    var body = new JObject();
                    body.Add("factor_id", factorId);
                    body.Add("passcode", mfaCode);
                    body.Add("transaction_id", loginInfo.FormFields["transaction_id"]);


                    using (var content = new StringContent(body.ToString(), System.Text.Encoding.UTF8, "application/json"))
                    {
                        HttpResponseMessage result = await client.PostAsync(client.BaseAddress + "oauth2/v3/authorize/mfa/verify", content);
                        string resultContent = await result.Content.ReadAsStringAsync();
                        if (!result.IsSuccessStatusCode)
                        {
                            ThrowException(result, resultContent);
                        }

                        var response = JObject.Parse(resultContent);
                        var data = response["data"];
                        if (data != null)
                        {
                            bool valid = data["valid"].Value<bool>();
                            if (!valid)
                            {
                                OnMFACodeInvalid(loginInfo.UserName, "MFA code is invalid");
                            }
                        }
                        else
                        {
                            var error = response["error"];
                            OnMFACodeInvalid(loginInfo.UserName, error["message"]?.ToString());
                        }
                    }
                }
            }
        }

        private static async Task<string> GetCodeAfterValidMfaAsync(LoginInfo loginInfo, TeslaAccountRegion region = TeslaAccountRegion.Unknown)
        {
            using (HttpClientHandler ch = new HttpClientHandler())
            {
                ch.AllowAutoRedirect = false;
                ch.UseCookies = false;
                using (HttpClient client = new HttpClient(ch))
                {
                    // client.Timeout = TimeSpan.FromSeconds(10);
                    client.BaseAddress = new Uri(GetBaseAddressForRegion(region));
                    client.DefaultRequestHeaders.Add("Cookie", loginInfo.Cookie);
                    client.DefaultRequestHeaders.Add("User-agent", UserAgent);

                    Dictionary<string, string> d = new Dictionary<string, string>();
                    d.Add("transaction_id", loginInfo.FormFields["transaction_id"]);

                    using (FormUrlEncodedContent content = new FormUrlEncodedContent(d))
                    {
                        UriBuilder b = new UriBuilder(client.BaseAddress + "oauth2/v3/authorize");
                        b.Port = -1;
                        var q = HttpUtility.ParseQueryString(b.Query);
                        q.Add("client_id", "ownerapi");
                        q.Add("code_challenge", loginInfo.CodeChallenge);
                        q.Add("code_challenge_method", "S256");
                        q.Add("redirect_uri", "https://auth.tesla.com/void/callback");
                        q.Add("response_type", "code");
                        q.Add("scope", "openid email offline_access");
                        q.Add("state", loginInfo.State);
                        b.Query = q.ToString();
                        string url = b.ToString();

                        var temp = content.ReadAsStringAsync().Result;

                        HttpResponseMessage result = await client.PostAsync(url, content);
                        string resultContent = await result.Content.ReadAsStringAsync();

                        Uri location = result.Headers.Location;

                        if (result.StatusCode == HttpStatusCode.Redirect && location != null)
                        {
                            return HttpUtility.ParseQueryString(location.Query).Get("code");
                        }
                        throw new Exception("Unable to get authorization code");
                    }
                }
            }
        }

        /// <summary>
        /// Should your Owner API token begin with "cn-" you should POST to auth.tesla.cn Tesla SSO service to have it refresh. Owner API tokens 
        /// starting with "qts-" are to be refreshed using auth.tesla.com                    
        /// </summary>
        /// <param name="region">Which Tesla server is this account created with?</param>
        /// <returns>Address like "https://auth.tesla.com", no trailing slash</returns>
        private static string GetBaseAddressForRegion(TeslaAccountRegion region)
        {
            switch (region)
            {
                case TeslaAccountRegion.Unknown:
                case TeslaAccountRegion.USA:
                    return "https://auth.tesla.com";

                case TeslaAccountRegion.China:
                    return "https://auth.tesla.cn";

                default:
                    throw new NotImplementedException("Fell threw switch in GetBaseAddressForRegion for " + region);
            }
        }

        private static DateTime ToDateTime(long unixTimestamp)
        {
            return new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc).AddSeconds(unixTimestamp);
        }

        private static TimeSpan FromUnixTimeSpan(long unixTimeSpan)
        {
            return TimeSpan.FromSeconds(unixTimeSpan);
        }
    }
}
