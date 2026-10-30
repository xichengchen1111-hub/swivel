using System;
using System.Threading.Tasks;
using Swivel.Core;

namespace Swivel.Windows;

public sealed partial class App
{
    private ScreenshotCardController? screenshotCards;
    private ScreenshotCardWindow? screenshotCard;

    /// Starts or stops the Screenshots-folder watcher to match Clothesline, the setting and Quit.
    private Task UpdateScreenshotCards()
    {
        if (validationHost) return Task.CompletedTask;
        screenshotCards ??= new ScreenshotCardController(
            () => ScreenshotCards.ShouldRun(Prefs.IsOn(Feature.Clothesline),Prefs.ScreenshotCards,exiting),
            ScreenshotCardController.Native,
            ShowScreenshotCard,
            () => screenshotCard?.Close(),
            message => { Home.SetStatus(L(message)); Home.Refresh(); });
        return screenshotCards.Update();
    }

    /// Why the watcher is not running after it stopped by itself, for the settings page.
    internal bool ScreenshotCardsHalted => screenshotCards?.Halted == true;

    private void ShowScreenshotCard(ArrivalCard<System.Windows.Media.Imaging.BitmapSource> card)
    {
        if (exiting || !Prefs.IsOn(Feature.Clothesline)) return;
        if (screenshotCard is null)
        {
            var window = new ScreenshotCardWindow(this,HangScreenshotAsync,id => screenshotCards?.Done(id));
            screenshotCard = window;
            window.Closed += (_,_) => { if (ReferenceEquals(screenshotCard,window)) screenshotCard = null; };
        }
        screenshotCard.Present(card);
    }

    /// Hangs the file by reference with the ordinary Clothesline action. Lowers the top-edge
    /// rope when that is on, otherwise opens the Clothesline window. The file is never moved.
    private Task HangScreenshotAsync(string path)
    {
        if (exiting || !Prefs.IsOn(Feature.Clothesline)) return Task.CompletedTask;
        return RunActionAsync(new("clothesline.add","Hang on Clothesline",Feature.Clothesline,ImageOnly: true),new[] { path },Home,
            message => Home.SetStatus(message),screenshotCard: true);
    }

    /// After a card was hung: the rope comes down on the pointer's monitor for a moment.
    private bool PresentRope()
    {
        if (ropeWatcher is null || RopeEdgeWatcher.Native(true) is not { FullScreen: false } sample) return false;
        ropeWatcher.Present(sample,TimeSpan.FromSeconds(2.5));
        return true;
    }

    private async Task FinishScreenshotCardsAsync()
    {
        await UpdateScreenshotCards();
        screenshotCard?.Close();
    }
}
