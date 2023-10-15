using OpenQA.Selenium;
using OpenQA.Selenium.DevTools;
using OpenQA.Selenium.DevTools.V114.Page;
using System.Reactive.Linq;

namespace CraigRec2Telegram
{
    public class CraigDownloader : ICraigDownloader
    {
        private readonly Func<string, IWebDriver> webDriverFactory;

        public CraigDownloader(Func<string, IWebDriver> webDriverFactory)
        {
            this.webDriverFactory = webDriverFactory;
        }

        public async Task<string> DownloadRecord(string url, string downloadFolder, Func<string, Task> setStatusText, CancellationToken cancel)
        {
            using var driver = webDriverFactory(downloadFolder);
            using var session = ((IDevTools)driver).GetDevToolsSession();
            var devToolsSession = session.GetVersionSpecificDomains<OpenQA.Selenium.DevTools.V114.DevToolsSessionDomains>();

            await devToolsSession.Network.Enable(new OpenQA.Selenium.DevTools.V114.Network.EnableCommandSettings(), cancel);
            await devToolsSession.Page.Enable(new EnableCommandSettings(), cancel);
            await devToolsSession.Page.SetDownloadBehavior(new SetDownloadBehaviorCommandSettings
            {
                Behavior = "allow",
                DownloadPath = downloadFolder
            }, cancel);

            var pageLoaded = devToolsSession.Network.GetResponseRecievedObservable()
                .FirstOrDefaultAsync(e => e.EventArgs.Response.Url == url).RunAsync(cancel);

            await setStatusText("Перехожу по ссылке...");
            driver.Navigate().GoToUrl(url);

            await pageLoaded;

            using var findElementCancelTimeout = new CancellationTokenSource(30_000);
            using var findElementCancel = CancellationTokenSource.CreateLinkedTokenSource(cancel, findElementCancelTimeout.Token);
            async Task<IWebElement> FindElementAsync(string xpath)
            {
                for (; ; )
                {
                    var elements = driver.FindElements(By.XPath(xpath));

                    if (elements.Count > 0)
                        return elements[0];

                    await Task.Delay(1000, findElementCancel.Token);
                }
            }

            await setStatusText("Жду обработки (1)...");
            var notFoundOrMpeg4Button = await FindElementAsync("//*[(name() = 'button' and .//span[text()='AAC'] and .//span[.//span[text()='(MPEG-4)']]and .//parent::div[.//preceding-sibling::div[.//h4[text() = 'Single-track mixed']]]) or (name() = 'span' and text() = 'Recording not found.')]");

            if (notFoundOrMpeg4Button.TagName == "button")
            {
                notFoundOrMpeg4Button.Click();
            }
            else
            {
                await setStatusText("Запись не найдена!");
                throw new ApplicationLogicException();
            }

            await setStatusText("Жду обработки (2)...");
            (await FindElementAsync("//button[text()='I understand.']")).Click();

            var downloadedFileName = devToolsSession.Page.GetDownloadCompletedFilesObservable()
                .Where(e => e.EventArgs.SuggestedFilename.EndsWith(".m4a"))
                .SelectMany(begin =>
                    devToolsSession.Page.GetDownloadProgressObservable()
                    .Where(r => r.EventArgs.Guid == begin.EventArgs.Guid && r.EventArgs.State == "completed")
                    .Select(_ => begin.EventArgs.SuggestedFilename))
                .FirstAsync()
                .RunAsync(cancel);

            for (int i = 0; i < 2; i++)
            {
                await setStatusText($"Жду обработки ({3 + i * 2})...");
                (await FindElementAsync("//button[contains(@class, 'row') and text()='OK' and .//parent::div[@class = 'dialog' and .//div[contains(text(), 'To handle large projects')]]]")).Click();
                await setStatusText($"Жду обработки ({4 + i * 2})...");
                (await FindElementAsync("//button[contains(@class, 'row') and text()='OK' and .//parent::div[@class = 'dialog' and .//div[text() = 'Failed to acquire permission for persistent storage. Large projects will fail.']]]")).Click();
            }

            for (bool finished = false; !finished;)
            {
                var elements = driver.FindElements(By.XPath("//div[not(contains(@style,'display:none'))]//*[(name() = 'div' and contains(@class, 'dialog') and (contains(text(),'Loading') or contains(text(),'Mix') or contains(text(),'Export'))) or (name() = 'button' and contains(@class, 'row') and text()='OK' and .//parent::div[@class = 'dialog' and .//div[text() = 'Your audio has now been exported. You may close this tab, or click OK to continue using this tool.']])]"));

                foreach (var buttonOrStatus in elements)
                {
                    if (buttonOrStatus.TagName == "button")
                    {
                        buttonOrStatus.Click();
                        finished = true;
                    }
                    else
                        await setStatusText(buttonOrStatus.Text);
                }

                if (!finished)
                    await Task.Delay(TimeSpan.FromSeconds(5), cancel);
            }

            return Path.Combine(downloadFolder, await downloadedFileName);
        }
    }
}
