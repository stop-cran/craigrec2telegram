using OpenQA.Selenium.DevTools;
using OpenQA.Selenium.DevTools.V114.Page;
using OpenQA.Selenium.DevTools.V114.Network;
using System.Reactive;
using System.Reactive.Linq;

namespace CraigRec2Telegram
{
    public static class DevToolExtensions
    {
        public static IObservable<EventPattern<DownloadWillBeginEventArgs>> GetDownloadCompletedFilesObservable(this PageAdapter pageAdapter) =>
            Observable.FromEventPattern<DownloadWillBeginEventArgs>(
                d => pageAdapter.DownloadWillBegin += d,
                d => pageAdapter.DownloadWillBegin -= d);

        public static IObservable<EventPattern<DownloadProgressEventArgs>> GetDownloadProgressObservable(this PageAdapter pageAdapter) =>
            Observable.FromEventPattern<DownloadProgressEventArgs>(
                d => pageAdapter.DownloadProgress += d,
                d => pageAdapter.DownloadProgress -= d);

        public static IObservable<EventPattern<ResponseReceivedEventArgs>> GetResponseRecievedObservable(this OpenQA.Selenium.DevTools.V114.Network.NetworkAdapter networkAdapter) =>
            Observable.FromEventPattern<ResponseReceivedEventArgs>(
                d => networkAdapter.ResponseReceived += d,
                d => networkAdapter.ResponseReceived -= d);
    }
}
