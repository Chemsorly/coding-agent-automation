namespace CodingAgentWebUI;

/// <summary>
/// Static dictionary of Lucide icon SVG path data (MIT license).
/// Each entry maps an icon name to its SVG inner content (path elements).
/// </summary>
public static class LucideIcons
{
    public static readonly IReadOnlyDictionary<string, string> Icons = new Dictionary<string, string>
    {
        ["message-circle"] = """<path d="M7.9 20A9 9 0 1 0 4 16.1L2 22Z"/>""",
        ["bot"] = """<path d="M12 8V4H8"/><rect width="16" height="12" x="4" y="8" rx="2"/><path d="M2 14h2"/><path d="M20 14h2"/><path d="M15 13v2"/><path d="M9 13v2"/>""",
        ["bar-chart-2"] = """<line x1="18" x2="18" y1="20" y2="10"/><line x1="12" x2="12" y1="20" y2="4"/><line x1="6" x2="6" y1="20" y2="14"/>""",
        ["refresh-cw"] = """<path d="M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8"/><path d="M21 3v5h-5"/><path d="M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16"/><path d="M3 21v-5h5"/>""",
        ["settings"] = """<path d="M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2z"/><circle cx="12" cy="12" r="3"/>""",
        ["info"] = """<circle cx="12" cy="12" r="10"/><path d="M12 16v-4"/><path d="M12 8h.01"/>""",
        ["check-circle"] = """<path d="M22 11.08V12a10 10 0 1 1-5.93-9.14"/><path d="m9 11 3 3L22 4"/>""",
        ["x-circle"] = """<circle cx="12" cy="12" r="10"/><path d="m15 9-6 6"/><path d="m9 9 6 6"/>""",
        ["alert-triangle"] = """<path d="m21.73 18-8-14a2 2 0 0 0-3.48 0l-8 14A2 2 0 0 0 4 21h16a2 2 0 0 0 1.73-3"/><path d="M12 9v4"/><path d="M12 17h.01"/>""",
        ["ban"] = """<circle cx="12" cy="12" r="10"/><path d="m4.9 4.9 14.2 14.2"/>""",
        ["circle"] = """<circle cx="12" cy="12" r="10"/>""",
        ["slash"] = """<circle cx="12" cy="12" r="10"/><line x1="4.93" y1="4.93" x2="19.07" y2="19.07"/>""",
        ["play"] = """<polygon points="6 3 20 12 6 21 6 3"/>""",
        ["chevron-down"] = """<path d="m6 9 6 6 6-6"/>""",
        ["chevron-right"] = """<path d="m9 18 6-6-6-6"/>""",
        ["search"] = """<circle cx="11" cy="11" r="8"/><path d="m21 21-4.3-4.3"/>""",
        ["file-text"] = """<path d="M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7Z"/><path d="M14 2v4a2 2 0 0 0 2 2h4"/><path d="M10 9H8"/><path d="M16 13H8"/><path d="M16 17H8"/>""",
    };
}
