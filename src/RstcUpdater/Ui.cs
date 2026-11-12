using System;
using System.Collections.Generic;
using System.Linq;
using Spectre.Console;

namespace RstcUpdater
{
    /// <summary>
    /// v5 终端视觉：红石红/暗色终端风格。
    /// 像素块 RST 标志 + 边框标题区 + 状态行 + 经典方向键菜单（致敬 v4.11 观感，但更整齐）。
    /// </summary>
    internal static class Ui
    {
        public const string Accent = "red";
        public const string Dim = "grey";

        private static readonly string[] Logo =
        {
            "██████   ██████  ████████",
            "██   ██ ██          ██   ",
            "██████  ██████      ██   ",
            "██  ██        ██    ██   ",
            "██   ██ ██████      ██   "
        };

        public static void LogoBox()
        {
            string title = "红石镇客户端更新器  v" + SelfUpdater.CurrentVersion;
            string subtitle = "Minecraft 26.3 · Fabric · " + ChannelLabel();
            int contentWidth = Math.Max(44, DisplayWidth(title) + 4);
            contentWidth = Math.Max(contentWidth, DisplayWidth(subtitle) + 4);
            contentWidth = Math.Max(contentWidth, Logo.Max(l => DisplayWidth(l)) + 6);

            string border = new string('─', contentWidth);
            AnsiConsole.MarkupLine("[" + Accent + "]┌" + border + "┐[/]");
            AnsiConsole.MarkupLine("[" + Accent + "]│[/]" + new string(' ', contentWidth) + "[" + Accent + "]│[/]");
            foreach (string line in Logo)
            {
                AnsiConsole.MarkupLine("[" + Accent + "]│[/]" + "[" + Accent + "]" +
                                       CenterDisplay(line, contentWidth) + "[/]" + "[" + Accent + "]│[/]");
            }

            AnsiConsole.MarkupLine("[" + Accent + "]│[/]" + new string(' ', contentWidth) + "[" + Accent + "]│[/]");
            AnsiConsole.MarkupLine("[" + Accent + "]│[/]" + "[white]" + CenterDisplay(title, contentWidth) + "[/]" +
                                   "[" + Accent + "]│[/]");
            AnsiConsole.MarkupLine("[" + Accent + "]│[/]" + "[" + Dim + "]" + CenterDisplay(subtitle, contentWidth) + "[/]" +
                                   "[" + Accent + "]│[/]");
            AnsiConsole.MarkupLine("[" + Accent + "]└" + border + "┘[/]");
            AnsiConsole.WriteLine();
        }

        private static string ChannelLabel()
        {
            return ReleaseClient.BetaChannel ? "Beta 内测通道" : "正式版通道";
        }

        public static void Section(string title)
        {
            int width = Math.Max(40, SafeWidth() - 2);
            string head = "› " + title + " ";
            int rest = Math.Max(0, width - DisplayWidth(head) - 2);
            AnsiConsole.MarkupLine("[" + Accent + "]" + Markup.Escape(head) + "[/]" + "[" + Dim + "]" + new string('─', rest) + "[/]");
        }

        public static void Rows(IEnumerable<(string Label, string ValueMarkup)> rows, int labelWidth = 14)
        {
            foreach (var row in rows)
            {
                string label = PadDisplay(row.Label, labelWidth);
                AnsiConsole.MarkupLine("  [" + Dim + "]" + Markup.Escape(label) + "[/] " + row.ValueMarkup);
            }
        }

        public static void Hint(string text)
        {
            AnsiConsole.MarkupLine("  [" + Dim + "]" + Markup.Escape(text) + "[/]");
        }

        public static string PadDisplay(string text, int width)
        {
            int current = DisplayWidth(text);
            if (current >= width)
            {
                return text;
            }

            return text + new string(' ', width - current);
        }

        public static string CenterDisplay(string text, int width)
        {
            int left = Math.Max(0, (width - DisplayWidth(text)) / 2);
            int right = Math.Max(0, width - DisplayWidth(text) - left);
            return new string(' ', left) + text + new string(' ', right);
        }

        /// <summary>CJK 字符按 2 列计算（近似，足以对齐中文标签）。</summary>
        public static int DisplayWidth(string text)
        {
            int width = 0;
            foreach (char c in text)
            {
                width += c >= 0x1100 && (c <= 0x115F || (c >= 0x2E80 && c <= 0xA4CF) ||
                                          (c >= 0xAC00 && c <= 0xD7A3) || (c >= 0xF900 && c <= 0xFAFF) ||
                                          (c >= 0xFE30 && c <= 0xFE6F) || (c >= 0xFF00 && c <= 0xFF60) ||
                                          (c >= 0xFFE0 && c <= 0xFFE6))
                    ? 2
                    : 1;
            }

            return width;
        }

        public static int SafeWidth()
        {
            try
            {
                int consoleWidth = Console.WindowWidth;
                return consoleWidth > 20 ? consoleWidth : 80;
            }
            catch (Exception)
            {
                return 80;
            }
        }

        /// <summary>按显示宽度截断文本（超出加 ...），用于游戏目录等长路径。</summary>
        public static string Fit(string text, int maxWidth)
        {
            if (string.IsNullOrEmpty(text) || maxWidth <= 0)
            {
                return text;
            }

            if (DisplayWidth(text) <= maxWidth)
            {
                return text;
            }

            var builder = new System.Text.StringBuilder();
            int width = 0;
            foreach (char c in text)
            {
                int charWidth = DisplayWidth(c.ToString());
                if (width + charWidth > maxWidth - 3)
                {
                    break;
                }

                builder.Append(c);
                width += charWidth;
            }

            return builder.Append("...").ToString();
        }
    }
}
