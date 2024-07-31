﻿using Midjourney.Infrastructure.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PuppeteerSharp;
using RestSharp;
using Serilog;

namespace Midjourney.Captcha.API
{
    /// <summary>
    /// CF 验证器
    /// </summary>
    public class CloudflareHelper
    {
        /// <summary>
        /// 验证 URL 模拟人机验证
        /// </summary>
        /// <param name="captchaOption"></param>
        /// <param name="hash"></param>
        /// <param name="url"></param>
        /// <returns></returns>
        public static async Task<bool> Validate(CaptchaOption captchaOption, string hash, string url)
        {
            IBrowser browser = null;
            IPage page = null;

            try
            {
                // 下载并设置浏览器
                await new BrowserFetcher().DownloadAsync();

                // 启动浏览器
                browser = await Puppeteer.LaunchAsync(new LaunchOptions
                {
                    Headless = captchaOption.Headless // 设置无头模式
                });

                //// 创建无痕浏览器上下文
                //var context = await browser.CreateBrowserContextAsync();

                //// 创建一个新的页面
                //page = await context.NewPageAsync();

                page = await browser.NewPageAsync();

                // 设置用户代理和添加初始化脚本以移除设备指纹信息
                await page.SetUserAgentAsync("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
                await page.EvaluateExpressionOnNewDocumentAsync(@"() => {
                    Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
                    Object.defineProperty(navigator, 'plugins', { get: () => [1, 2, 3] });
                    Object.defineProperty(navigator, 'languages', { get: () => ['en-US', 'en'] });
                    Object.defineProperty(navigator, 'platform', { get: () => 'Win32' });
                    Object.defineProperty(navigator, 'userAgent', { get: () => 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36' });
                }");

                // 收集所有请求的URL
                var urls = new List<string>();
                await page.SetRequestInterceptionAsync(true);
                page.Request += (sender, e) =>
                {
                    urls.Add(e.Request.Url);
                    e.Request.ContinueAsync();
                };

                // 等待
                await Task.Delay(500);

                // 打开 url
                await page.GoToAsync(url);

                // 等待
                await Task.Delay(6000);

                // 日志
                Log.Information("CF 验证 URL: {@0}", url);

                var siteKeyCount = 0;
                var siteKey = string.Empty;
                do
                {
                    if (siteKeyCount > 20)
                    {
                        // 超时没有获取到 sitekey
                        return false;
                    }

                    // 获取 Cloudflare 验证页面的 src
                    var src = urls.FirstOrDefault(c => c.StartsWith("https://challenges.cloudflare.com/cdn-cgi/challenge-platform"));
                    siteKey = src?.Split("/").Where(c => c.StartsWith("0x") && c.Length > 20).FirstOrDefault();

                    if (!string.IsNullOrWhiteSpace(siteKey))
                    {
                        break;
                    }

                    siteKeyCount++;
                    await Task.Delay(1000);
                } while (true);

                // 日志
                Log.Information("CF 验证 SiteKey: {@0}", siteKey);

                var taskId = string.Empty;
                var taskCount = 0;
                do
                {
                    if (taskCount > 20)
                    {
                        // 超时没有获取到 taskId
                        return false;
                    }

                    // 使用 RestSharp 调用 2Captcha API 解决验证码
                    var client = new RestClient();
                    var request = new RestRequest("https://api.2captcha.com/createTask", Method.Post);
                    request.AddHeader("Content-Type", "application/json");
                    var body = new
                    {
                        clientKey = captchaOption.TwoCaptchaKey,
                        task = new
                        {
                            type = "TurnstileTaskProxyless",
                            websiteURL = url,
                            websiteKey = siteKey
                        }
                    };

                    var json = JsonConvert.SerializeObject(body);
                    request.AddStringBody(json, DataFormat.Json);
                    var response = await client.ExecuteAsync(request);
                    if (response.StatusCode == System.Net.HttpStatusCode.OK)
                    {
                        var obj = JsonConvert.DeserializeObject<JObject>(response.Content);
                        if (obj.ContainsKey("taskId"))
                        {
                            taskId = obj["taskId"].ToString();

                            if (!string.IsNullOrWhiteSpace(taskId))
                            {
                                break;
                            }
                        }
                    }

                    taskCount++;
                    await Task.Delay(1000);
                } while (true);

                // 日志
                Log.Information("CF 验证 TaskId: {@0}", taskId);

                // 等待
                await Task.Delay(6000);

                var token = string.Empty;
                var tokenCount = 0;

                do
                {
                    if (tokenCount > 60)
                    {
                        // 超时没有获取到 token
                        return false;
                    }

                    var client = new RestClient();
                    var request = new RestRequest("https://api.2captcha.com/getTaskResult", Method.Post);
                    request.AddHeader("Content-Type", "application/json");
                    var body = new
                    {
                        clientKey = captchaOption.TwoCaptchaKey,
                        taskId = taskId
                    };
                    var json = JsonConvert.SerializeObject(body);
                    request.AddStringBody(json, DataFormat.Json);
                    var response = await client.ExecuteAsync(request);
                    if (response.StatusCode == System.Net.HttpStatusCode.OK)
                    {
                        var obj = JsonConvert.DeserializeObject<JObject>(response.Content);
                        if (obj.ContainsKey("solution"))
                        {
                            token = obj["solution"]["token"].ToString();
                            if (!string.IsNullOrWhiteSpace(token))
                            {
                                break;
                            }
                        }
                    }

                    tokenCount++;
                    await Task.Delay(1000);
                } while (true);

                // 日志
                Log.Information("CF 验证 Token: {@0}", token);

                // 提交到 mj 服务器
                if (string.IsNullOrWhiteSpace(token))
                {
                    var retry = 0;
                    do
                    {
                        if (retry > 3)
                        {
                            break;
                        }

                        retry++;
                        var options = new RestClientOptions("")
                        {
                            MaxTimeout = -1,
                        };
                        var client = new RestClient(options);
                        var request = new RestRequest($"https://editor.midjourney.com/captcha/api/c/{hash}/submit", Method.Post);
                        request.AlwaysMultipartFormData = true;
                        request.AddParameter("captcha_token", token);
                        var response = await client.ExecuteAsync(request);
                        if (response.StatusCode == System.Net.HttpStatusCode.OK)
                        {
                            return true;
                        }
                    } while (true);
                }

                await Task.Delay(1000);

                // 日志
                Log.Information("CF 验证失败 {@0}", url);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "CF 验证 URL 异常 {@0}", url);
            }
            finally
            {
                // 关闭浏览器
                await browser.CloseAsync();
            }

            return false;
        }
    }
}