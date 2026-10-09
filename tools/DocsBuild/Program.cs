using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace LabControl.DocsBuild;

/// <summary>
/// Regenerates <c>docs/html/</c> from the Markdown sources. The Markdown files are the
/// single source of truth; the HTML is a mirror and must never be edited by hand.
/// Pages are fully self-contained (CSS inlined, no external requests) so they open by
/// double-click from Finder/Explorer on a machine with no internet.
/// </summary>
internal static class Program
{
    /// <summary>Documents in the order they should appear in the navigation.</summary>
    private static readonly (string RelativePath, string NavTitle)[] Ordered =
    [
        ("README.md", "Overview"),
        ("CLAUDE.md", "Claude project brief"),
        ("AGENTS.md", "Codex instructions"),
        ("docs/ARCHITECTURE.md", "Architecture"),
        ("docs/PROTOCOL.md", "Protocol"),
        ("docs/INSTALLER.md", "Installer"),
        ("docs/DEVELOPMENT.md", "Development"),
        ("docs/ROADMAP.md", "Roadmap"),
        ("docs/DECISIONS.md", "Decisions"),
    ];

    private static int Main(string[] args)
    {
        try
        {
            var root = ResolveRoot(args.Length > 0 ? args[0] : null);
            var outputDir = Path.Combine(root, "docs", "html");
            Directory.CreateDirectory(outputDir);

            var pipeline = new MarkdownPipelineBuilder()
                .UseAdvancedExtensions()
                .UseAutoIdentifiers()
                .Build();

            var pages = CollectPages(root);
            if (pages.Count == 0)
            {
                Console.Error.WriteLine("No Markdown sources found under " + root);
                return 1;
            }

            var generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

            foreach (var page in pages)
            {
                var markdown = File.ReadAllText(Path.Combine(root, page.RelativePath));
                var document = Markdown.Parse(markdown, pipeline);

                page.Title = FirstHeadingText(document) ?? page.NavTitle;
                page.Summary = FirstParagraphText(document);
                page.Toc = BuildToc(document);
                page.Body = PostProcess(RewriteLinks(Markdown.ToHtml(markdown, pipeline), page, pages));
            }

            foreach (var page in pages)
            {
                var html = RenderPage(page, pages, generatedAt);
                File.WriteAllText(Path.Combine(outputDir, page.OutputName), html, new UTF8Encoding(false));
                Console.WriteLine($"  {page.RelativePath,-24} -> docs/html/{page.OutputName}");
            }

            var index = RenderIndex(pages, generatedAt);
            File.WriteAllText(Path.Combine(outputDir, "index.html"), index, new UTF8Encoding(false));
            Console.WriteLine($"  {"(index)",-24} -> docs/html/index.html");
            Console.WriteLine($"{pages.Count + 1} pages written to docs/html/");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("docs-build failed: " + ex.Message);
            return 1;
        }
    }

    /// <summary>Walks up from the current directory (or the argument) to the repository root.</summary>
    private static string ResolveRoot(string? candidate)
    {
        var dir = new DirectoryInfo(candidate ?? Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")) &&
                File.Exists(Path.Combine(dir.FullName, "AGENTS.md")) &&
                Directory.Exists(Path.Combine(dir.FullName, "docs")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the repository root (a directory containing CLAUDE.md, AGENTS.md and docs/). " +
            "Pass it as the first argument.");
    }

    /// <summary>Ordered documents first, then any other docs/*.md alphabetically.</summary>
    private static List<Page> CollectPages(string root)
    {
        var pages = new List<Page>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (relativePath, navTitle) in Ordered)
        {
            if (!File.Exists(Path.Combine(root, relativePath)))
            {
                continue;
            }

            pages.Add(new Page(relativePath, navTitle));
            seen.Add(relativePath);
        }

        var extra = Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md")
            .Select(p => "docs/" + Path.GetFileName(p))
            .Where(p => !seen.Contains(p))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);

        foreach (var relativePath in extra)
        {
            var name = Path.GetFileNameWithoutExtension(relativePath);
            pages.Add(new Page(relativePath, CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant())));
        }

        return pages;
    }

    private static string? FirstHeadingText(MarkdownDocument document) =>
        document.Descendants<HeadingBlock>().FirstOrDefault(h => h.Level == 1) is { } heading
            ? InlineText(heading.Inline)
            : null;

    private static string FirstParagraphText(MarkdownDocument document)
    {
        var paragraph = document.Descendants<ParagraphBlock>().FirstOrDefault();
        var text = paragraph is null ? string.Empty : InlineText(paragraph.Inline);
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length <= 180 ? text : text[..177].TrimEnd() + "…";
    }

    private static List<TocEntry> BuildToc(MarkdownDocument document) =>
        document.Descendants<HeadingBlock>()
            .Where(h => h.Level is 2 or 3)
            .Select(h => new TocEntry(h.Level, InlineText(h.Inline), h.GetAttributes().Id ?? string.Empty))
            .Where(e => e.Id.Length > 0)
            .ToList();

    private static string InlineText(ContainerInline? container)
    {
        if (container is null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var inline in container.Descendants())
        {
            switch (inline)
            {
                case LiteralInline literal:
                    sb.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    sb.Append(code.Content);
                    break;
                case LineBreakInline:
                    sb.Append(' ');
                    break;
            }
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Markdown links are relative to their source file (README.md at the root, the rest in
    /// docs/), while the mirror lives in docs/html/. A link to a mirrored .md opens its page;
    /// any other relative link or image points back at the file in the repository.
    /// </summary>
    private static string RewriteLinks(string html, Page page, IReadOnlyList<Page> pages)
    {
        // The mirror makes no external requests: a remote image (a README badge) becomes its alt text.
        html = Regex.Replace(html, "<img\\s[^>]*src=\"https?://[^>]*>", m =>
        {
            var alt = Regex.Match(m.Value, "alt=\"([^\"]*)\"");
            return alt.Success ? $"<span class=\"badge\">{alt.Groups[1].Value}</span>" : string.Empty;
        });

        var sourceDir = Path.GetDirectoryName(page.RelativePath)?.Replace('\\', '/') ?? string.Empty;
        return Regex.Replace(html, "(href|src)=\"([^\"]*)\"", m =>
        {
            var target = WebUtility.HtmlDecode(m.Groups[2].Value);
            if (target.Length == 0 || target[0] is '#' or '/' || Regex.IsMatch(target, "^[A-Za-z][A-Za-z0-9+.-]*:"))
            {
                return m.Value;
            }

            var hash = target.IndexOf('#');
            var path = hash < 0 ? target : target[..hash];
            var fragment = hash < 0 ? string.Empty : target[hash..];
            var repoPath = NormalizePath(sourceDir.Length == 0 ? path : sourceDir + "/" + path);
            var mirrored = pages.FirstOrDefault(p => string.Equals(p.RelativePath, repoPath, StringComparison.OrdinalIgnoreCase));
            var rewritten = mirrored is not null ? mirrored.OutputName + fragment
                : repoPath.StartsWith("docs/html/", StringComparison.Ordinal) ? repoPath["docs/html/".Length..] + fragment
                : "../../" + repoPath + fragment;
            return $"{m.Groups[1].Value}=\"{Encode(rewritten)}\"";
        });
    }

    /// <summary>Resolves "." and ".." segments of a repository-relative path, keeping a trailing slash.</summary>
    private static string NormalizePath(string path)
    {
        var segments = new List<string>();
        foreach (var segment in path.Split('/'))
        {
            if (segment is "" or ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                continue;
            }

            segments.Add(segment);
        }

        var joined = string.Join('/', segments);
        return path.EndsWith('/') && joined.Length > 0 ? joined + "/" : joined;
    }

    /// <summary>
    /// Wraps tables so wide ones scroll inside the page instead of stretching it, and adds a
    /// hover anchor to every h2/h3 so sections can be linked directly.
    /// </summary>
    private static string PostProcess(string html)
    {
        html = html.Replace("<table>", "<div class=\"table-wrap\"><table>", StringComparison.Ordinal)
                   .Replace("</table>", "</table></div>", StringComparison.Ordinal);

        html = Regex.Replace(
            html,
            "<h([23]) id=\"([^\"]+)\">(.*?)</h\\1>",
            m => $"<h{m.Groups[1].Value} id=\"{m.Groups[2].Value}\">{m.Groups[3].Value}" +
                 $"<a class=\"anchor\" href=\"#{m.Groups[2].Value}\" aria-label=\"Link to this section\">#</a></h{m.Groups[1].Value}>",
            RegexOptions.Singleline);

        return html;
    }

    private static string RenderPage(Page page, List<Page> pages, string generatedAt)
    {
        var toc = new StringBuilder();
        if (page.Toc.Count > 1)
        {
            toc.Append("<nav class=\"toc\" aria-label=\"On this page\"><p class=\"toc-title\">On this page</p><ul>");
            foreach (var entry in page.Toc)
            {
                toc.Append($"<li class=\"lvl{entry.Level}\"><a href=\"#{entry.Id}\">{Encode(entry.Text)}</a></li>");
            }

            toc.Append("</ul></nav>");
        }

        var main = $"""
                    <article class="doc">
                      <p class="source">Generated from <code>{Encode(page.RelativePath)}</code> — do not edit this file; edit the Markdown and run <code>tools/docs-build.sh</code>.</p>
                      {page.Body}
                    </article>
                    """;

        return Shell(page.Title, Nav(pages, page.OutputName), toc.ToString(), main, generatedAt);
    }

    private static string RenderIndex(List<Page> pages, string generatedAt)
    {
        var cards = new StringBuilder();
        foreach (var page in pages)
        {
            cards.Append($"""
                          <a class="card" href="{page.OutputName}">
                            <span class="card-kicker">{Encode(page.NavTitle)}</span>
                            <span class="card-title">{Encode(page.Title)}</span>
                            <span class="card-summary">{Encode(page.Summary)}</span>
                            <span class="card-source">{Encode(page.RelativePath)}</span>
                          </a>
                          """);
        }

        var main = $"""
                    <article class="doc">
                      <h1>LabControl documentation</h1>
                      <p class="lead">Classroom fleet control for one computer lab: a cross-platform teacher
                      console driving 14 Windows student PCs over the local network.</p>
                      <p class="source">This is a generated mirror of the Markdown documentation. The
                      <code>.md</code> files in the repository are the source of truth; regenerate these pages
                      with <code>tools/docs-build.sh</code> after every change.</p>
                      <div class="cards">{cards}</div>
                    </article>
                    """;

        return Shell("LabControl documentation", Nav(pages, "index.html"), string.Empty, main, generatedAt);
    }

    private static string Nav(List<Page> pages, string current)
    {
        var sb = new StringBuilder();
        sb.Append("<nav class=\"side\" aria-label=\"Documents\">");
        sb.Append("<a class=\"brand\" href=\"index.html\"><span class=\"brand-mark\">LC</span><span class=\"brand-name\">LabControl</span><span class=\"brand-sub\">documentation</span></a>");
        sb.Append("<ul>");
        foreach (var page in pages)
        {
            var active = string.Equals(page.OutputName, current, StringComparison.OrdinalIgnoreCase) ? " class=\"active\"" : string.Empty;
            sb.Append($"<li><a href=\"{page.OutputName}\"{active}>{Encode(page.NavTitle)}</a></li>");
        }

        sb.Append("</ul></nav>");
        return sb.ToString();
    }

    private static string Shell(string title, string nav, string toc, string main, string generatedAt) =>
        $"""
         <!doctype html>
         <html lang="en">
         <head>
         <meta charset="utf-8">
         <meta name="viewport" content="width=device-width, initial-scale=1">
         <title>{Encode(title)} · LabControl</title>
         <style>{Css}</style>
         </head>
         <body>
         <div class="layout">
         {nav}
         <main>
         {main}
         <footer>Generated {generatedAt} by <code>tools/docs-build.sh</code>. Source of truth: the Markdown files in the repository.</footer>
         </main>
         {toc}
         </div>
         </body>
         </html>
         """;

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    private const string Css = """
        :root{
          --bg:#f7f7f5; --panel:#ffffff; --ink:#1c1c1a; --muted:#6b6b64; --line:#e2e2dc;
          --accent:#0b6b53; --accent-soft:#e6f1ed; --code-bg:#f2f2ee; --mark:#fff6d6;
          --radius:10px; --sans:-apple-system,BlinkMacSystemFont,"Segoe UI",Inter,Roboto,"Helvetica Neue",Arial,sans-serif;
          --mono:ui-monospace,SFMono-Regular,"SF Mono",Menlo,Consolas,"Liberation Mono",monospace;
        }
        @media (prefers-color-scheme: dark){
          :root{
            --bg:#16171a; --panel:#1d1f23; --ink:#e6e6e2; --muted:#9a9a92; --line:#2e3138;
            --accent:#5ec2a3; --accent-soft:#1e2a27; --code-bg:#22252a; --mark:#3a3320;
          }
        }
        *{box-sizing:border-box}
        html{-webkit-text-size-adjust:100%}
        body{margin:0;background:var(--bg);color:var(--ink);font-family:var(--sans);
             font-size:16px;line-height:1.65;-webkit-font-smoothing:antialiased}
        .layout{display:grid;grid-template-columns:248px minmax(0,1fr) 232px;gap:0;
                max-width:1400px;margin:0 auto;align-items:start}

        /* ---- sidebar ---- */
        .side{position:sticky;top:0;height:100vh;overflow-y:auto;padding:28px 20px;
              border-right:1px solid var(--line)}
        .brand{display:block;text-decoration:none;color:inherit;margin-bottom:26px}
        .brand-mark{display:inline-flex;align-items:center;justify-content:center;width:34px;height:34px;
                    border-radius:9px;background:var(--accent);color:#fff;font-weight:700;font-size:14px;
                    letter-spacing:.5px;margin-bottom:10px}
        .brand-name{display:block;font-weight:650;font-size:17px;letter-spacing:-.01em}
        .brand-sub{display:block;color:var(--muted);font-size:12.5px;letter-spacing:.02em}
        .side ul{list-style:none;margin:0;padding:0}
        .side li{margin:1px 0}
        .side a{display:block;padding:7px 10px;border-radius:7px;text-decoration:none;
                color:var(--muted);font-size:14.5px}
        .side a:hover{background:var(--code-bg);color:var(--ink)}
        .side a.active{background:var(--accent-soft);color:var(--accent);font-weight:600}

        /* ---- content ---- */
        main{padding:44px 48px 72px;min-width:0}
        .doc{max-width:820px}
        h1{font-size:2.05rem;line-height:1.2;letter-spacing:-.02em;margin:0 0 .5em;font-weight:680}
        h2{font-size:1.4rem;letter-spacing:-.01em;margin:2.4em 0 .6em;padding-bottom:.3em;
           border-bottom:1px solid var(--line);font-weight:640}
        h3{font-size:1.11rem;margin:1.9em 0 .5em;font-weight:640}
        h4{font-size:1rem;margin:1.5em 0 .4em;color:var(--muted);text-transform:uppercase;
           letter-spacing:.06em;font-size:.8rem}
        p,ul,ol{margin:0 0 1.05em}
        ul,ol{padding-left:1.35em}
        li{margin:.28em 0}
        li>ul,li>ol{margin:.3em 0}
        a{color:var(--accent);text-decoration:none;border-bottom:1px solid transparent}
        a:hover{border-bottom-color:currentColor}
        strong{font-weight:640}
        hr{border:0;border-top:1px solid var(--line);margin:2.6em 0}
        .lead{font-size:1.1rem;color:var(--muted);max-width:60ch}
        .source{font-size:.83rem;color:var(--muted);background:var(--code-bg);
                border:1px solid var(--line);border-radius:var(--radius);padding:9px 13px;margin-bottom:2em}
        .anchor{margin-left:.4em;color:var(--muted);opacity:0;font-weight:400;border:0;font-size:.85em}
        h2:hover .anchor,h3:hover .anchor{opacity:.55}
        .anchor:hover{opacity:1 !important}

        /* ---- code ---- */
        code{font-family:var(--mono);font-size:.875em;background:var(--code-bg);
             border:1px solid var(--line);border-radius:5px;padding:.1em .36em}
        pre{background:var(--code-bg);border:1px solid var(--line);border-radius:var(--radius);
            padding:14px 16px;overflow-x:auto;margin:0 0 1.3em;line-height:1.5}
        pre code{background:none;border:0;padding:0;font-size:.845rem;white-space:pre}
        blockquote{margin:0 0 1.2em;padding:.1em 0 .1em 1.1em;border-left:3px solid var(--accent);
                   color:var(--muted)}

        img{max-width:100%;height:auto}
        .badge{display:inline-block;font-size:.8rem;color:var(--muted);border:1px solid var(--line);
               border-radius:5px;padding:.05em .45em;margin:0 .15em}
        .mermaid{font-family:var(--mono);font-size:.845rem;white-space:pre;overflow-x:auto;
                 background:var(--code-bg);border:1px solid var(--line);border-radius:var(--radius);
                 padding:14px 16px;margin:0 0 1.3em;line-height:1.5}

        /* ---- tables ---- */
        .table-wrap{overflow-x:auto;margin:0 0 1.5em;border:1px solid var(--line);
                    border-radius:var(--radius);background:var(--panel)}
        table{border-collapse:collapse;width:100%;font-size:.91rem}
        th,td{text-align:left;padding:9px 13px;border-bottom:1px solid var(--line);vertical-align:top}
        th{background:var(--code-bg);font-weight:640;white-space:nowrap}
        tbody tr:last-child td{border-bottom:0}
        td code{white-space:nowrap}

        /* ---- task lists ---- */
        input[type=checkbox]{margin-right:.5em;accent-color:var(--accent)}
        li.task-list-item{list-style:none;margin-left:-1.2em}

        /* ---- on-this-page ---- */
        .toc{position:sticky;top:0;max-height:100vh;overflow-y:auto;padding:48px 22px 40px;
             border-left:1px solid var(--line);font-size:13.5px}
        .toc-title{margin:0 0 .7em;color:var(--muted);text-transform:uppercase;
                   letter-spacing:.07em;font-size:11px;font-weight:640}
        .toc ul{list-style:none;margin:0;padding:0}
        .toc li{margin:.3em 0}
        .toc a{color:var(--muted);display:block;line-height:1.35}
        .toc a:hover{color:var(--accent)}
        .toc .lvl3{padding-left:12px;font-size:12.8px}

        /* ---- index cards ---- */
        .cards{display:grid;grid-template-columns:repeat(auto-fill,minmax(240px,1fr));gap:14px;margin-top:2em}
        .card{display:flex;flex-direction:column;gap:5px;padding:16px 17px;background:var(--panel);
              border:1px solid var(--line);border-radius:var(--radius);text-decoration:none;color:inherit}
        .card:hover{border-color:var(--accent);box-shadow:0 2px 14px rgba(0,0,0,.06)}
        .card-kicker{color:var(--accent);font-size:11px;text-transform:uppercase;letter-spacing:.07em;font-weight:650}
        .card-title{font-weight:640;font-size:1.02rem}
        .card-summary{color:var(--muted);font-size:.86rem;line-height:1.45}
        .card-source{margin-top:6px;color:var(--muted);font-family:var(--mono);font-size:11.5px}

        footer{margin-top:5em;padding-top:1.3em;border-top:1px solid var(--line);
               color:var(--muted);font-size:12.5px;max-width:820px}

        /* ---- responsive ---- */
        @media (max-width:1180px){
          .layout{grid-template-columns:230px minmax(0,1fr)}
          .toc{display:none}
        }
        @media (max-width:820px){
          .layout{grid-template-columns:1fr}
          .side{position:static;height:auto;border-right:0;border-bottom:1px solid var(--line);padding:20px}
          .side ul{display:flex;flex-wrap:wrap;gap:4px}
          main{padding:28px 22px 56px}
        }
        @media print{
          .side,.toc,.anchor{display:none}
          .layout{display:block}
          main{padding:0}
          a{color:inherit}
          pre,.table-wrap{break-inside:avoid}
        }
        """;

    private sealed class Page(string relativePath, string navTitle)
    {
        public string RelativePath { get; } = relativePath;
        public string NavTitle { get; } = navTitle;
        public string OutputName { get; } = Path.GetFileNameWithoutExtension(relativePath) + ".html";
        public string Title { get; set; } = navTitle;
        public string Summary { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public List<TocEntry> Toc { get; set; } = [];
    }

    private sealed record TocEntry(int Level, string Text, string Id);
}
