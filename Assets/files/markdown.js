// The docs markdown renderer: window.renderMarkdown in the page, module.exports under node for a check.
(function (root) {
  const escapeHtml = (text) => text.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
  // A relative href against the markdown file's folder; null for a scheme, an anchor or a malformed escape.
  const resolveRelative = (filePath, href) => {
    if (/^[a-z][a-z0-9+.-]*:/i.test(href) || href.startsWith("#")) return null;
    const folder = filePath.includes("/") ? filePath.slice(0, filePath.lastIndexOf("/") + 1) : "";
    try {
      // URL parsing drops tabs and newlines ("java\tscript:") and honours "//host", so only a URL left on the base is relative.
      const url = new URL(href, "https://root/" + folder);
      return url.origin === "https://root" ? decodeURIComponent(url.pathname.slice(1)) : null;
    } catch {
      return null;
    }
  };
  let current = null; // { group, path } of the file being rendered
  root.marked.use({ renderer: {
    // Docs come from Jira and other people: raw HTML shows as text and never runs; only a line break passes.
    html({ text, block }) {
      if (/^<br\s*\/?>$/i.test(text.trim())) return "<br>";
      return block ? `<pre class="raw">${escapeHtml(text)}</pre>` : escapeHtml(text);
    },
    image({ href, text }) {
      const path = resolveRelative(current.path, href);
      return path === null
        ? `<span class="alt">[${escapeHtml(text || href)}]</span>`
        : `<img data-group="${escapeHtml(current.group)}" data-path="${escapeHtml(path)}" alt="${escapeHtml(text)}">`;
    },
    // Only web and relative links become anchors; javascript:, data:, file: and the like stay plain text.
    link({ href, tokens }) {
      const label = this.parser.parseInline(tokens);
      return /^https?:\/\//i.test(href) || resolveRelative(current.path, href) !== null
        ? `<a href="${escapeHtml(href)}">${label}</a>`
        : label;
    },
  }});
  const renderMarkdown = (text, file) => {
    current = file;
    try {
      return root.marked.parse(text);
    } finally {
      current = null;
    }
  };
  root.renderMarkdown = renderMarkdown;
  root.resolveRelative = resolveRelative;
  if (typeof module !== "undefined") module.exports = { renderMarkdown, resolveRelative };
})(typeof window !== "undefined" ? window : globalThis);
