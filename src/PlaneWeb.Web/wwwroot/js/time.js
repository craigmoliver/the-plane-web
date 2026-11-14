// The browser's IANA time zone (e.g. "Europe/London"), so the server can show times in the viewer's zone.
window.planeWebTimeZone = () => {
    try { return Intl.DateTimeFormat().resolvedOptions().timeZone || null; } catch { return null; }
};
