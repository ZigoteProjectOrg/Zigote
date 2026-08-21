using CoreGraphics;
using Foundation;
using UIKit;

namespace Share;

/// <summary>
///     iOS implementation — <c>UIActivityViewController</c>, the system share sheet, presented
///     over whatever the app already shows. It reports back: a completed activity is
///     <see cref="ShareStatus.Success" />, a swipe-away is <see cref="ShareStatus.Dismissed" />.
/// </summary>
internal static class ShareDriver
{
    public static Task<ShareStatus> ShareAsync(string? text, string? subject, string[] paths)
    {
        var items = new List<NSObject>();
        if (!string.IsNullOrWhiteSpace(text)) items.Add(new NSString(text));
        foreach (string path in paths)
            if (NSUrl.FromFilename(path) is { } url)
                items.Add(url);
        if (items.Count == 0) return Task.FromResult(ShareStatus.Unavailable);

        var tcs = new TaskCompletionSource<ShareStatus>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        UIApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            try
            {
                if (RootController() is not { } root)
                {
                    tcs.TrySetResult(ShareStatus.Unavailable);
                    return;
                }

                var sheet = new UIActivityViewController(items.ToArray(), null);
                // The documented way to give mail targets a subject line.
                if (!string.IsNullOrWhiteSpace(subject))
                    sheet.SetValueForKey(new NSString(subject), new NSString("subject"));

                // iPad presents the sheet as a popover and crashes without an anchor; the middle
                // of the screen with no arrow is the honest anchor for a share that came from
                // app code rather than from a known button.
                // ponytail: no caller-supplied origin rect (share_plus's sharePositionOrigin).
                // Add a parameter when an app wants the popover pinned to its own button.
                if (sheet.PopoverPresentationController is { } popover)
                {
                    popover.SourceView = root.View;
                    CGRect bounds = root.View!.Bounds;
                    popover.SourceRect = new CGRect(bounds.GetMidX(), bounds.GetMidY(), 0, 0);
                    popover.PermittedArrowDirections = (UIPopoverArrowDirection)0;
                }

                sheet.CompletionWithItemsHandler = (_, completed, _, _) =>
                    tcs.TrySetResult(completed ? ShareStatus.Success : ShareStatus.Dismissed);
                root.PresentViewController(sheet, true, null);
            }
            catch (Exception)
            {
                tcs.TrySetResult(ShareStatus.Unavailable);
            }
        });
        return tcs.Task;
    }

    /// <summary>The controller on top of the foreground window — what a modal must be presented from.</summary>
    private static UIViewController? RootController()
    {
        UIWindow? window = UIApplication.SharedApplication.ConnectedScenes
            .OfType<UIWindowScene>()
            .SelectMany(scene => scene.Windows)
            .FirstOrDefault(w => w.IsKeyWindow);
        UIViewController? controller = window?.RootViewController;
        while (controller?.PresentedViewController is { } presented) controller = presented;
        return controller;
    }
}
