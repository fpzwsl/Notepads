// ---------------------------------------------------------------------------------------------
//  Syntax highlighting for common source and data files.
// ---------------------------------------------------------------------------------------------

namespace Notepads.Controls.TextEditor
{
    using System;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;
    using Windows.UI;
    using Windows.UI.Xaml;

    public partial class TextEditorCore
    {
        private static readonly HashSet<string> SyntaxHighlightExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".c", ".h", ".cc", ".cpp", ".cxx", ".cs", ".java", ".go", ".rs", ".swift", ".kt", ".kts",
            ".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx", ".vue", ".py", ".rb", ".php", ".lua",
            ".sh", ".bash", ".zsh", ".ps1", ".psm1", ".sql", ".json", ".jsonc", ".xml", ".xaml", ".html", ".htm",
            ".css", ".scss", ".less", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf", ".md", ".markdown"
        };

        private static readonly Regex SyntaxTokenRegex = new Regex(
            @"(?<comment>//[^\r\n]*|/\*[\s\S]*?\*/|#[^\r\n]*|<!--[\s\S]*?-->)|(?<string>""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'|`(?:\\.|[^`\\])*`)|(?<number>\b(?:0[xX][0-9a-fA-F](?:_?[0-9a-fA-F])*|0[bB][01](?:_?[01])*|\d(?:_?\d)*(?:\.\d(?:_?\d)*)?(?:[eE][+-]?\d(?:_?\d)*)?)(?:[fFdDmMlLuU]+)?\b)|(?<keyword>\b(?:abstract|as|async|await|break|case|catch|class|const|continue|def|delete|do|else|enum|export|extends|false|finally|for|from|function|get|if|implements|import|in|interface|is|let|lock|namespace|new|null|of|override|package|private|protected|public|readonly|return|sealed|set|static|struct|switch|this|throw|throws|true|try|typeof|using|var|virtual|void|while|with|yield|SELECT|FROM|WHERE|INSERT|UPDATE|DELETE|CREATE|ALTER|DROP|AND|OR|NOT|NULL)\b)|(?<type>\b(?:bool|boolean|byte|char|decimal|double|dynamic|float|int|long|object|sbyte|short|string|uint|ulong|ushort|void|var|List|Dictionary|Task|String|Integer|Number|Boolean)\b)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private DispatcherTimer _syntaxHighlightTimer;
        private string _syntaxLanguage = string.Empty;
        private bool _syntaxHighlightingApplied;
        private bool _isApplyingSyntaxHighlighting;
        private string _lastHighlightedText;

        internal void InitializeSyntaxHighlighting()
        {
            _syntaxHighlightTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            _syntaxHighlightTimer.Tick += SyntaxHighlightTimer_Tick;
            Notepads.Services.ThemeSettingsService.OnThemeChanged += SyntaxThemeChanged;
        }

        internal void DisposeSyntaxHighlighting()
        {
            if (_syntaxHighlightTimer != null)
            {
                _syntaxHighlightTimer.Stop();
                _syntaxHighlightTimer.Tick -= SyntaxHighlightTimer_Tick;
            }

            Notepads.Services.ThemeSettingsService.OnThemeChanged -= SyntaxThemeChanged;
        }

        public void SetSyntaxLanguage(string extension)
        {
            var normalized = string.IsNullOrWhiteSpace(extension) ? string.Empty : extension.Trim();
            if (!normalized.StartsWith(".")) normalized = "." + normalized;
            if (string.Equals(_syntaxLanguage, normalized, StringComparison.OrdinalIgnoreCase)) return;

            _syntaxLanguage = normalized;
            ScheduleSyntaxHighlighting();
        }

        private void SyntaxThemeChanged(object sender, ElementTheme theme)
        {
            ScheduleSyntaxHighlighting();
        }

        private void SyntaxHighlightTimer_Tick(object sender, object e)
        {
            _syntaxHighlightTimer.Stop();
            ApplySyntaxHighlighting();
        }

        private void ScheduleSyntaxHighlighting()
        {
            if (_syntaxHighlightTimer == null || _isApplyingSyntaxHighlighting) return;
            _syntaxHighlightTimer.Stop();
            _syntaxHighlightTimer.Start();
        }

        private void ApplySyntaxHighlighting()
        {
            if (_isApplyingSyntaxHighlighting || Document == null) return;
            var text = GetText();
            if (text == null || text.Length > 200000) return;

            var hasSyntax = SyntaxHighlightExtensions.Contains(_syntaxLanguage);
            if (!hasSyntax && !_syntaxHighlightingApplied) return;
            if (hasSyntax && _syntaxHighlightingApplied && string.Equals(text, _lastHighlightedText, StringComparison.Ordinal)) return;

            _isApplyingSyntaxHighlighting = true;
            try
            {
                var selectionStart = Document.Selection.StartPosition;
                var selectionEnd = Document.Selection.EndPosition;
                var wholeDocument = Document.GetRange(0, text.Length);
                wholeDocument.CharacterFormat.ForegroundColor = GetSyntaxColor("text");

                if (hasSyntax)
                {
                    foreach (Match token in SyntaxTokenRegex.Matches(text))
                    {
                        var group = token.Groups["comment"].Success ? "comment" :
                            token.Groups["string"].Success ? "string" :
                            token.Groups["number"].Success ? "number" :
                            token.Groups["keyword"].Success ? "keyword" : "type";
                        Document.GetRange(token.Index, token.Index + token.Length).CharacterFormat.ForegroundColor = GetSyntaxColor(group);
                    }
                }

                Document.Selection.SetRange(Math.Min(selectionStart, text.Length), Math.Min(selectionEnd, text.Length));
                _syntaxHighlightingApplied = hasSyntax;
                _lastHighlightedText = hasSyntax ? text : null;
            }
            catch (Exception ex)
            {
                Notepads.Services.LoggingService.LogError($"[{nameof(TextEditorCore)}] Syntax highlighting failed: {ex.Message}");
            }
            finally
            {
                _isApplyingSyntaxHighlighting = false;
            }
        }

        private Color GetSyntaxColor(string tokenType)
        {
            var dark = Notepads.Services.ThemeSettingsService.ThemeMode == ElementTheme.Dark;
            switch (tokenType)
            {
                case "comment": return dark ? Color.FromArgb(255, 106, 153, 85) : Color.FromArgb(255, 0, 128, 0);
                case "string": return dark ? Color.FromArgb(255, 214, 157, 133) : Color.FromArgb(255, 163, 21, 21);
                case "number": return dark ? Color.FromArgb(255, 181, 206, 168) : Color.FromArgb(255, 9, 134, 88);
                case "keyword": return dark ? Color.FromArgb(255, 86, 156, 214) : Color.FromArgb(255, 0, 0, 255);
                case "type": return dark ? Color.FromArgb(255, 78, 201, 176) : Color.FromArgb(255, 43, 145, 175);
                default:
                    try { return (Color)Application.Current.Resources[dark ? "SystemBaseHighColor" : "SystemBaseHighColor"]; }
                    catch { return dark ? Colors.White : Colors.Black; }
            }
        }
    }
}
