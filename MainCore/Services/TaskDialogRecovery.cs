namespace MainCore.Services;

public static class TaskDialogRecovery
{
    public static Task<bool> CloseNpcAsync(IChromeBrowser browser) => CloseAsync(browser, npcOnly: true);
    public static Task<bool> CloseLocalAsync(IChromeBrowser browser) => CloseAsync(browser, npcOnly: false);

    private static async Task<bool> CloseAsync(IChromeBrowser browser, bool npcOnly)
    {
        if (!browser.IsInitialized) return true;
        try
        {
            var npc = browser.CurrentPage.Locator(".dialogOverlay:visible:has(#npc)");
            var hero = browser.CurrentPage.Locator("div.resourceTransferDialog:visible");
            var npcCount = await npc.CountAsync();
            var heroCount = npcOnly ? 0 : await hero.CountAsync();
            if (npcCount == 0 && heroCount == 0) return true;
            var root = npcCount == 1 && heroCount == 0 ? npc : heroCount == 1 && npcCount == 0 ? hero : null;
            if (root is not null)
            {
                var cancel = root.Locator(".dialogCancelButton");
                if (await cancel.CountAsync() == 1 && await cancel.IsVisibleAsync() && await cancel.IsEnabledAsync())
                {
                    await cancel.ClickAsync(new() { Timeout = 2000 });
                    await npc.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 1000 });
                    if (!npcOnly) await hero.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 1000 });
                    if (await npc.CountAsync() == 0 && (npcOnly || await hero.CountAsync() == 0)) return true;
                }
            }
            return await SafeOverviewAsync(browser);
        }
        catch { return await SafeOverviewAsync(browser); }
    }

    private static async Task<bool> SafeOverviewAsync(IChromeBrowser browser)
    {
        try
        {
            if (!Uri.TryCreate(browser.CurrentUrl, UriKind.Absolute, out var current) || current.Scheme is not ("http" or "https")) return false;
            var result = await browser.Navigate(new Uri(current, "/dorf1.php").ToString());
            return result.IsSuccess && await LoginParser.IsIngamePage(browser.CurrentPage)
                && await browser.CurrentPage.Locator(".dialogOverlay:visible:has(#npc), div.resourceTransferDialog:visible").CountAsync() == 0;
        }
        catch { return false; }
    }
}
