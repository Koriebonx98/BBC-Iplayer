using System;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace bbc_iplayer_app
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // Persistent WebView2 user data folder
                string userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "bbc_iplayer_app",
                    "WebView2UserData");

                Directory.CreateDirectory(userDataFolder);

                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await webView.EnsureCoreWebView2Async(env);

                webView.CoreWebView2.NavigationCompleted += (s, args) =>
                {
                    string script = @"
(function () {
    // Helpers
    function safeClick(el, win) {
        if (!el) return false;
        try {
            el.click();
            return true;
        } catch (e) {
            try {
                const evt = new MouseEvent('click', { bubbles: true, cancelable: true, view: win || window });
                el.dispatchEvent(evt);
                return true;
            } catch (e2) {
                return false;
            }
        }
    }

    function tryRequestFullscreenOnVideo(video) {
        if (!video) return false;
        try {
            if (video.requestFullscreen) { video.requestFullscreen(); return true; }
            if (video.webkitEnterFullscreen) { video.webkitEnterFullscreen(); return true; }
            const parent = video.parentElement || video;
            if (parent.requestFullscreen) { parent.requestFullscreen(); return true; }
        } catch (e) {}
        return false;
    }

    function tryRequestFullscreenOnIframe(iframe) {
        if (!iframe) return false;
        try {
            if (iframe.requestFullscreen) { iframe.requestFullscreen(); return true; }
        } catch (e) {}
        return false;
    }

    function clickFullscreenSvgInRoot(root) {
        try {
            // 1) SVG with class enter_fullscreen_button
            const svg = (root || document).querySelector && (root || document).querySelector('svg.enter_fullscreen_button');
            if (svg) {
                // Prefer clicking a parent button if present
                const btn = svg.closest && svg.closest('button, [role=""button""]');
                if (btn) { safeClick(btn, window); return true; }

                // Otherwise click the SVG itself
                safeClick(svg, window);
                return true;
            }

            // 2) Common fullscreen buttons (icons inside buttons)
            const candidates = (root || document).querySelectorAll ? Array.from((root || document).querySelectorAll('button, [role=""button""]')) : [];
            for (const c of candidates) {
                try {
                    const aria = (c.getAttribute && (c.getAttribute('aria-label') || '') || '').toLowerCase();
                    const txt = (c.innerText || '').toLowerCase();
                    if (aria.includes('fullscreen') || txt.includes('fullscreen') || c.querySelector && c.querySelector('svg.enter_fullscreen_button')) {
                        safeClick(c, window);
                        return true;
                    }
                } catch (e) {}
            }
        } catch (e) {}
        return false;
    }

    // Search shadow roots recursively for selector
    function findInShadow(root, selector) {
        try {
            if (!root) return null;
            const direct = root.querySelector(selector);
            if (direct) return direct;
            const all = root.querySelectorAll('*');
            for (const node of all) {
                try {
                    if (node.shadowRoot) {
                        const found = node.shadowRoot.querySelector(selector);
                        if (found) return found;
                        const nested = findInShadow(node.shadowRoot, selector);
                        if (nested) return nested;
                    }
                } catch (e) {}
            }
        } catch (e) {}
        return null;
    }

    // Find skip button in a document-like root
    function findSkipButtonInRoot(root) {
        if (!root) return null;
        try {
            // exact class
            let btn = root.querySelector && root.querySelector('button.skip-trailer__button');
            if (btn) return btn;

            // wrapper
            let wrap = root.querySelector && root.querySelector('.skip-trailer');
            if (wrap) {
                let b = wrap.querySelector('button');
                if (b) return b;
            }

            // shadow DOM
            let sbtn = findInShadow(root, 'button.skip-trailer__button');
            if (sbtn) return sbtn;
            let swrap = findInShadow(root, '.skip-trailer button');
            if (swrap) return swrap;

            // text fallback
            const buttons = root.querySelectorAll ? Array.from(root.querySelectorAll('button')) : [];
            for (const b of buttons) {
                try {
                    const text = (b.innerText || '').toLowerCase();
                    const aria = (b.getAttribute && (b.getAttribute('aria-label') || '') || '').toLowerCase();
                    if (text.includes('skip trailer') || text.includes('skip intro') || text.includes('skip recap') || text === 'skip' || aria.includes('skip')) {
                        return b;
                    }
                } catch (e) {}
            }

            // spans/divs containing the text then closest button
            const texts = root.querySelectorAll ? Array.from(root.querySelectorAll('span,div')) : [];
            for (const el of texts) {
                try {
                    const t = (el.innerText || '').toLowerCase();
                    if (t.includes('skip trailer') || t.includes('skip intro') || t.includes('skip recap')) {
                        const parentBtn = el.closest ? el.closest('button') : null;
                        if (parentBtn) return parentBtn;
                    }
                } catch (e) {}
            }
        } catch (e) {}
        return null;
    }

    // After clicking skip, attempt to make player fullscreen by multiple strategies
    function makePlayerFullscreen(contextIframe) {
        try {
            // 1) Try to find a visible video in the same document and request fullscreen
            const doc = contextIframe && contextIframe.contentDocument ? contextIframe.contentDocument : document;
            try {
                let video = null;
                const vids = Array.from(doc.querySelectorAll('video'));
                if (vids.length) video = vids.find(v => v.offsetParent !== null) || vids[0];
                if (video && tryRequestFullscreenOnVideo(video)) return true;
            } catch (e) {}

            // 2) Try to click the SVG fullscreen control in the same root
            if (clickFullscreenSvgInRoot(doc)) return true;

            // 3) If we were given an iframe element, request fullscreen on the iframe (works cross-origin)
            if (contextIframe && tryRequestFullscreenOnIframe(contextIframe)) return true;

            // 4) Top-level fallback: click any visible fullscreen control in top document
            if (clickFullscreenSvgInRoot(document)) return true;

            // 5) Last resort: request fullscreen on documentElement
            try {
                if (document.documentElement && document.documentElement.requestFullscreen) {
                    document.documentElement.requestFullscreen();
                    return true;
                }
            } catch (e) {}
        } catch (e) {}
        return false;
    }

    // Main scan: find skip button, click it, then attempt fullscreen
    function scanAndHandleSkip() {
        try {
            // Top document
            const topBtn = findSkipButtonInRoot(document);
            if (topBtn) {
                const clicked = safeClick(topBtn, window);
                makePlayerFullscreen(null);
                return clicked;
            }

            // Search near video elements in top doc
            try {
                const videos = Array.from(document.querySelectorAll('video'));
                for (const v of videos) {
                    try {
                        const container = v.closest ? v.closest('div,section,article') : null;
                        const root = container || document;
                        const res = findSkipButtonInRoot(root);
                        if (res) {
                            const clicked = safeClick(res, window);
                            tryRequestFullscreenOnVideo(v);
                            return clicked;
                        }
                    } catch (e) {}
                }
            } catch (e) {}

            // Iframes
            const iframes = Array.from(document.querySelectorAll('iframe'));
            for (const iframe of iframes) {
                try {
                    const idoc = iframe.contentDocument || (iframe.contentWindow && iframe.contentWindow.document);
                    if (idoc) {
                        const res = findSkipButtonInRoot(idoc);
                        if (res) {
                            // click inside iframe context
                            const clicked = safeClick(res, iframe.contentWindow || window);
                            // try fullscreen on iframe element (works cross-origin)
                            tryRequestFullscreenOnIframe(iframe);
                            // try to fullscreen any video inside iframe
                            try {
                                const vids = Array.from(idoc.querySelectorAll('video'));
                                if (vids.length) tryRequestFullscreenOnVideo(vids.find(v => v.offsetParent !== null) || vids[0]);
                            } catch (e) {}
                            if (clicked) return true;
                        }
                    }
                } catch (e) {
                    // cross-origin iframe: cannot access DOM. Try requesting fullscreen on the iframe element if visible.
                    try {
                        if (iframe && iframe.requestFullscreen) {
                            iframe.requestFullscreen();
                            return true;
                        }
                    } catch (e2) {}
                }
            }
        } catch (e) {}
        return false;
    }

    // MutationObserver + periodic scan
    try {
        const observer = new MutationObserver(() => { scanAndHandleSkip(); });
        observer.observe(document.documentElement || document, { childList: true, subtree: true });
    } catch (e) {}

    const periodic = setInterval(() => { scanAndHandleSkip(); }, 250);
    setTimeout(scanAndHandleSkip, 50);

    // Expose helper for debugging
    try { window.__autoSkipIPlayer = { scan: scanAndHandleSkip }; } catch (e) {}
})();
";
                    webView.CoreWebView2.ExecuteScriptAsync(script);
                };

                // Navigate to BBC iPlayer
                webView.CoreWebView2.Navigate("https://www.bbc.co.uk/iplayer");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"WebView2 failed to initialize:\n{ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
            }
        }
    }
}
