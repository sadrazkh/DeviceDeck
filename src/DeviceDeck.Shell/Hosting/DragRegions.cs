using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Input;
using Windows.Graphics;

namespace DeviceDeck.Shell.Hosting;

/// <summary>
/// Makes CSS <c>app-region: drag</c> move the window. A script injected into every page reports the
/// bounding boxes of drag and no-drag elements near the top of the page; they become the window's
/// Caption and Passthrough regions (InputNonClientPointerSource), so dragging, double-click to maximise,
/// the system menu and Snap all behave like a native title bar.
/// </summary>
internal sealed class DragRegions(WindowId window)
{
    public const string MessageName = "regions";

    /// <summary>Only elements starting this close to the top are measured (CSS px); the title bar lives there.</summary>
    private const int ScanDepth = 160;

    public int Updates { get; private set; }

    public RectInt32[] Caption { get; private set; } = [];

    public RectInt32[] Passthrough { get; private set; } = [];

    public void Apply(JsonElement message)
    {
        var scale = message.TryGetProperty("dpr", out var dpr) && dpr.TryGetDouble(out var d) && d > 0 ? d : 1.0;
        Caption = Read(message, "drag", scale);
        Passthrough = Read(message, "noDrag", scale);
        var source = InputNonClientPointerSource.GetForWindowId(window);
        source.SetRegionRects(NonClientRegionKind.Caption, Caption);
        source.SetRegionRects(NonClientRegionKind.Passthrough, Passthrough);
        Updates++;
    }

    public void Clear()
    {
        var source = InputNonClientPointerSource.GetForWindowId(window);
        source.SetRegionRects(NonClientRegionKind.Passthrough, []);
        source.SetRegionRects(NonClientRegionKind.Caption, []);
        Caption = [];
        Passthrough = [];
    }

    private static RectInt32[] Read(JsonElement message, string name, double scale)
    {
        if (!message.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var rects = new List<RectInt32>();
        foreach (var r in list.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Array || r.GetArrayLength() != 4)
            {
                continue;
            }

            var x = r[0].GetDouble() * scale;
            var y = r[1].GetDouble() * scale;
            var w = r[2].GetDouble() * scale;
            var h = r[3].GetDouble() * scale;
            if (w >= 1 && h >= 1)
            {
                rects.Add(new RectInt32((int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Ceiling(w), (int)Math.Ceiling(h)));
            }
        }

        return rects.Take(64).ToArray();
    }

    /// <summary>Runs in every document before its own scripts; posts {"$dd":"regions", drag, noDrag, dpr} when the boxes change.</summary>
    public static string Script => """
        (() => {
          if (window.__deviceDeckRegions || !window.chrome || !window.chrome.webview) return;
          window.__deviceDeckRegions = true;
          const depth = __DEPTH__;
          const regionOf = (el) => {
            const cs = getComputedStyle(el);
            return (cs.getPropertyValue('app-region') || cs.getPropertyValue('-webkit-app-region') || '').trim();
          };
          let last = '', queued = false;
          const measure = () => {
            queued = false;
            const drag = [], noDrag = [];
            const root = document.body;
            if (root) {
              const walker = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT, {
                acceptNode: (el) => el.getBoundingClientRect().top > depth ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT,
              });
              const seen = new Map();
              for (let el = walker.nextNode(); el; el = walker.nextNode()) {
                const region = regionOf(el);
                seen.set(el, region);
                // app-region inherits: only the element where it changes adds a box.
                if (region !== 'drag' && region !== 'no-drag') continue;
                if (seen.get(el.parentElement) === region) continue;
                const r = el.getBoundingClientRect();
                if (r.width < 1 || r.height < 1) continue;
                (region === 'drag' ? drag : noDrag).push([r.left, r.top, r.width, r.height].map(v => Math.round(v * 100) / 100));
              }
            }
            const key = JSON.stringify([drag, noDrag, devicePixelRatio]);
            if (key === last) return;
            last = key;
            window.chrome.webview.postMessage({ $dd: 'regions', drag, noDrag, dpr: devicePixelRatio });
          };
          const schedule = () => { if (!queued) { queued = true; setTimeout(() => requestAnimationFrame(measure), 50); } };
          const start = () => {
            schedule();
            new MutationObserver(schedule).observe(document.documentElement, { subtree: true, childList: true, attributes: true, characterData: false });
            addEventListener('resize', schedule);
            addEventListener('transitionend', schedule, true);
            addEventListener('animationend', schedule, true);
            matchMedia(`(resolution: ${devicePixelRatio}dppx)`).addEventListener?.('change', schedule);
            if (document.fonts) document.fonts.ready.then(schedule);
          };
          if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, { once: true });
          else start();
        })();
        """.Replace("__DEPTH__", ScanDepth.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
