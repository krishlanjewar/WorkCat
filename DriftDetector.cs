using System;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;

namespace WorkCat
{
    /// <summary>
    /// Holds the detection result with metadata for the cat's navigation and strike system.
    /// </summary>
    public class DriftTarget
    {
        public IntPtr Hwnd { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string MatchedPattern { get; set; } = string.Empty;
        public Win32Helper.RECT WindowRect { get; set; }
        public Point StrikePoint { get; set; }
    }

    /// <summary>
    /// Scans the foreground application via UI Automation and Win32 APIs for short-form video "drift" content.
    /// </summary>
    public class DriftDetector
    {
        private static readonly string[] KnownBrowserProcesses =
        {
            "chrome", "msedge", "brave", "firefox", "opera", "vivaldi", "arc", "waterfox", "librewolf"
        };

        private static readonly string[] DriftUrlPatterns =
        {
            "youtube.com/shorts",
            "tiktok.com",
            "instagram.com/reels"
        };

        private static readonly string[] DriftTitleKeywords =
        {
            "Shorts",
            "YouTube Shorts",
            "Reels",
            "TikTok",
            "Instagram Reels"
        };

        /// <summary>
        /// Asynchronously inspects the active foreground window for drift content.
        /// </summary>
        public async Task<DriftTarget?> CheckForegroundWindowAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    IntPtr fgHwnd = Win32Helper.GetForegroundWindow();
                    if (fgHwnd == IntPtr.Zero) return null;

                    if (!Win32Helper.GetWindowRect(fgHwnd, out Win32Helper.RECT rect))
                    {
                        return null;
                    }

                    // Exclude zero-size or off-screen minimized windows
                    if (rect.Width < 200 || rect.Height < 200) return null;

                    string windowTitle = Win32Helper.GetWindowTitle(fgHwnd);
                    Win32Helper.GetWindowThreadProcessId(fgHwnd, out uint processId);

                    string processName = string.Empty;
                    try
                    {
                        using var process = Process.GetProcessById((int)processId);
                        processName = process.ProcessName.ToLowerInvariant();
                    }
                    catch
                    {
                        // Process might have terminated or elevated permissions
                    }

                    bool isBrowser = KnownBrowserProcesses.Contains(processName);
                    string detectedUrl = string.Empty;
                    string matchedPattern = string.Empty;

                    // 1. Inspect URL if it's a known browser via UI Automation
                    if (isBrowser)
                    {
                        detectedUrl = ExtractBrowserUrl(fgHwnd, processName);
                        if (!string.IsNullOrEmpty(detectedUrl))
                        {
                            foreach (var pattern in DriftUrlPatterns)
                            {
                                if (detectedUrl.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    matchedPattern = $"URL: {pattern}";
                                    break;
                                }
                            }
                        }
                    }

                    // 2. Fallback: Check Window Title for explicit drift keywords
                    if (string.IsNullOrEmpty(matchedPattern) && !string.IsNullOrEmpty(windowTitle))
                    {
                        foreach (var keyword in DriftTitleKeywords)
                        {
                            // Use word boundary or exact token matching where applicable
                            if (Regex.IsMatch(windowTitle, $@"\b{Regex.Escape(keyword)}\b", RegexOptions.IgnoreCase))
                            {
                                matchedPattern = $"Title: {keyword}";
                                break;
                            }
                        }
                    }

                    // If drift content is detected, compute optimal strike position (top tab bar / header)
                    if (!string.IsNullOrEmpty(matchedPattern))
                    {
                        // Target near the top-middle/tab region of the window
                        double targetX = Math.Max(rect.Left + 80, Math.Min(rect.Right - 80, rect.CenterX));
                        double targetY = Math.Max(rect.Top + 40, rect.Top + 60);

                        return new DriftTarget
                        {
                            Hwnd = fgHwnd,
                            Title = windowTitle,
                            Url = detectedUrl,
                            MatchedPattern = matchedPattern,
                            WindowRect = rect,
                            StrikePoint = new Point(targetX, targetY)
                        };
                    }
                }
                catch
                {
                    // Fail silently to preserve continuous scanning loop
                }

                return null;
            });
        }

        /// <summary>
        /// Attempts to extract the active URL from Chromium & Firefox address bars using UI Automation.
        /// </summary>
        private string ExtractBrowserUrl(IntPtr hwnd, string processName)
        {
            try
            {
                var element = AutomationElement.FromHandle(hwnd);
                if (element == null) return string.Empty;

                // Fast search for Edit / Document controls with URL bar identifiers
                System.Windows.Automation.Condition editCondition = new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document)
                );

                var editElements = element.FindAll(TreeScope.Descendants, editCondition);
                foreach (AutomationElement edit in editElements)
                {
                    string automationId = edit.Current.AutomationId ?? string.Empty;
                    string name = edit.Current.Name ?? string.Empty;

                    // Chromium & Firefox known address bar identifiers
                    bool isAddressBar =
                        automationId.Equals("address-edit-box", StringComparison.OrdinalIgnoreCase) ||
                        automationId.Equals("urlbar-input", StringComparison.OrdinalIgnoreCase) ||
                        name.IndexOf("Address and search bar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("Search with Google or enter address", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("Search or enter address", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("Search or enter web address", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        edit.Current.ControlType == ControlType.Edit;

                    if (isAddressBar)
                    {
                        string text = GetElementValue(edit);
                        if (LooksLikeUrl(text))
                        {
                            return text;
                        }
                    }
                }
            }
            catch
            {
                // UI Automation exceptions occur if browser window is being resized or navigated
            }

            return string.Empty;
        }

        /// <summary>
        /// Extracts the text value from an AutomationElement across ValuePattern and TextPattern.
        /// </summary>
        private string GetElementValue(AutomationElement element)
        {
            try
            {
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object valPatternObj) &&
                    valPatternObj is ValuePattern valPattern)
                {
                    return valPattern.Current.Value ?? string.Empty;
                }

                if (element.TryGetCurrentPattern(TextPattern.Pattern, out object textPatternObj) &&
                    textPatternObj is TextPattern textPattern)
                {
                    return textPattern.DocumentRange.GetText(256)?.Trim() ?? string.Empty;
                }
            }
            catch
            {
                // Ignore transient COM exceptions
            }

            return string.Empty;
        }

        private bool LooksLikeUrl(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            return text.Contains(".com") ||
                   text.Contains(".org") ||
                   text.Contains(".net") ||
                   text.Contains("://") ||
                   text.StartsWith("http") ||
                   text.StartsWith("www.") ||
                   text.Contains("tiktok") ||
                   text.Contains("youtube") ||
                   text.Contains("instagram");
        }
    }
}
