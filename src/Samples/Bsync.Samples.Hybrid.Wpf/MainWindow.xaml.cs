using System.IO;
using System.Text.Json;
using System.Windows;
using Bsync.Client;
using Bsync.Samples.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Bsync.Samples.Hybrid.Wpf;

public partial class MainWindow : Window
{
    private readonly SyncSession<Note> _session;

    public MainWindow()
    {
        InitializeComponent();
        WebView.Services = App.Services;
        _session = App.Services.GetRequiredService<SyncSession<Note>>();

        // Native lifecycle: no replication while minimized; resuming syncs at once (ADR-007).
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
            {
                _session.Pause();
            }
            else
            {
                _session.Resume();
            }
        };

        if (App.Settings.SmokeResult is { } resultFile)
        {
            WebView.BlazorWebViewInitialized += async (_, _) => await RunSmokeAsync(resultFile);
        }
    }

    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        await _session.DisposeAsync();
    }

    /// <summary>
    /// Automated check used by the tests. It drives the real UI inside WebView2 and writes "ok" or the failure to the result
    /// file, then closes the app. Steps: <c>online</c> adds a note and waits until it is uploaded; <c>offline</c> adds a
    /// note while the server is unreachable and closes with it pending; <c>resume</c> starts again and waits until the note
    /// from the previous run is uploaded.
    /// </summary>
    private async Task RunSmokeAsync(string resultFile)
    {
        string result;
        try
        {
            var web = WebView.WebView.CoreWebView2;
            async Task<string> Eval(string script) => JsonSerializer.Deserialize<string>(await web.ExecuteScriptAsync(script)) ?? string.Empty;
            async Task Until(string condition, string what)
            {
                for (var i = 0; i < 600; i++)
                {
                    if (await Eval($"String(!!({condition}))") == "true")
                    {
                        return;
                    }

                    await Task.Delay(100);
                }

                throw new TimeoutException($"Timed out waiting for {what}. Page: {await Eval("document.body.innerText")}");
            }

            if (App.Settings.SmokeStep.StartsWith("library-", StringComparison.Ordinal))
            {
                await Until("document.querySelector('[data-testid=library-one]') && !document.querySelector('[data-testid=library-one]').disabled", "the content library");
                async Task Click(string id) => await Eval($"document.querySelector('[data-testid={id}]').click(), 'clicked'");
                Task Active(string expected) => Until($"document.querySelector('[data-testid=library-status]').textContent.includes('{expected}')", expected);
                if (App.Settings.SmokeStep == "library-prepare")
                {
                    await Click("library-one"); await Active("Active: 1; latest: 1; ReadyFreshnessUnknown");
                    await Click("library-interrupt"); await Active("Active: 1; latest: 2; ReadyKnownOutdated");
                }
                else
                {
                    await Active("Active: 1; latest: 2; ReadyKnownOutdated");
                    await Click("library-read");
                    await Until("document.querySelector('[data-testid=library-text]').textContent.includes('Revision 1:')", "the old complete guide");
                    await Click("library-two"); await Active("Active: 2; latest: 2; ReadyFreshnessUnknown");
                    await Click("library-read");
                    await Until("document.querySelector('[data-testid=library-text]').textContent.includes('Revision 2:')", "the updated complete guide");
                }
                await File.WriteAllTextAsync(resultFile, "ok content-library"); Close(); return;
            }
            await Until("document.querySelector('[data-testid=notes-panel]') && !document.querySelector('[data-testid=new-text]').disabled", "the notes panel");
            var text = App.Settings.SmokeText ?? $"from WPF {Guid.NewGuid():N}";
            if (App.Settings.SmokeStep == "resume")
            {
                await Until($"[...document.querySelectorAll('[data-testid=note-text]')].some(n => n.textContent === '{text}')", "the note from the previous run");
                await Until("document.querySelector('[data-testid=pending]').textContent === '0'", "the upload after restart");
                await File.WriteAllTextAsync(resultFile, $"ok {text}");
                Close();
                return;
            }

            await Eval($$"""
                (() => {
                  const input = document.querySelector('[data-testid=new-text]');
                  input.value = '{{text}}';
                  input.dispatchEvent(new Event('input', { bubbles: true }));
                  return 'typed';
                })()
                """);
            await Until("!document.querySelector('[data-testid=add]').disabled", "the add button");
            await Eval("document.querySelector('[data-testid=add]').click(), 'clicked'");
            await Until($"[...document.querySelectorAll('[data-testid=note-text]')].some(n => n.textContent === '{text}')", "the note");
            if (App.Settings.SmokeStep == "offline")
            {
                await Until("document.querySelector('[data-testid=pending]').textContent === '1' && document.querySelector('[data-testid=status]').textContent.startsWith('Offline')", "the offline state");
                await File.WriteAllTextAsync(resultFile, $"ok {text}");
                Close();
                return;
            }

            await Until("document.querySelector('[data-testid=pending]').textContent === '0'", "the upload");
            result = $"ok {text} host={await Eval("document.querySelector('[data-testid=host]').textContent")} renderer={await Eval("document.querySelector('[data-testid=renderer]').textContent")}";
        }
        catch (Exception error)
        {
            result = $"error {error}";
        }

        await File.WriteAllTextAsync(resultFile, result);
        Close();
    }
}
