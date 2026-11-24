"""Build spec.html from SPEC.md, so the artifact page always matches the spec file.

Usage (from the docs folder):  python -I build_spec_html.py
Supports the Markdown subset SPEC.md uses: headings, paragraphs, bold, inline code,
links, nested bullet and numbered lists, tables, block quotes and horizontal rules.
"""
import html
import re
from pathlib import Path

HERE = Path(__file__).resolve().parent
MD = (HERE / "SPEC.md").read_text(encoding="utf-8")
OLD = (HERE / "spec.html").read_text(encoding="utf-8")
STYLE = OLD[OLD.index("<style>"): OLD.index("</style>") + len("</style>")]
FONTS = OLD[: OLD.index("<style>")].split("\n")
FONTS = "\n".join(l for l in FONTS if l.startswith("<link") or l.startswith("<meta"))


def inline(text):
    codes = []

    def keep_code(m):
        codes.append(m.group(1))
        return "\x00%d\x00" % (len(codes) - 1)

    text = re.sub(r"`([^`]+)`", keep_code, text)
    text = html.escape(text, quote=False)
    text = re.sub(r"\[([^\]]+)\]\(([^)\s]+)\)", lambda m: '<a href="%s">%s</a>' % (html.escape(m.group(2)), m.group(1)), text)
    text = re.sub(r"\*\*(.+?)\*\*", r"<strong>\1</strong>", text)
    text = re.sub(r"\x00(\d+)\x00", lambda m: "<code>%s</code>" % html.escape(codes[int(m.group(1))], quote=False), text)
    return text


ITEM = re.compile(r"^(\s*)([-*]|\d+\.)\s+(.*)$")


def render_block(lines):
    """Render one block of non-heading Markdown lines to HTML."""
    out, i = [], 0
    while i < len(lines):
        line = lines[i]
        if not line.strip() or line.strip() == "---":
            i += 1
            continue
        if line.lstrip().startswith("|"):
            rows = []
            while i < len(lines) and lines[i].lstrip().startswith("|"):
                rows.append(lines[i].strip())
                i += 1
            cells = [[c.strip() for c in r.strip("|").split("|")] for r in rows if not re.match(r"^\|[\s:|-]+\|$", r)]
            head, body = cells[0], cells[1:]
            t = '<div class="tbl"><table><thead><tr>' + "".join("<th>%s</th>" % inline(c) for c in head) + "</tr></thead><tbody>"
            t += "".join("<tr>" + "".join("<td>%s</td>" % inline(c) for c in r) + "</tr>" for r in body)
            out.append(t + "</tbody></table></div>")
            continue
        if line.startswith(">"):
            quote = []
            while i < len(lines) and lines[i].startswith(">"):
                quote.append(lines[i].lstrip("> ").rstrip())
                i += 1
            text = " ".join(q for q in quote if q)
            cls = "note legal" if "משפטי" in text else "note"
            out.append('<div class="%s"><p>%s</p></div>' % (cls, inline(text)))
            continue
        if ITEM.match(line):
            block = []
            while i < len(lines) and lines[i].strip() and (ITEM.match(lines[i]) or lines[i].startswith(" ")):
                block.append(lines[i])
                i += 1
            out.append(render_list(block))
            continue
        para = [line.strip()]
        i += 1
        while i < len(lines) and lines[i].strip() and not ITEM.match(lines[i]) and not lines[i].lstrip().startswith("|") and not lines[i].startswith(">"):
            para.append(lines[i].strip())
            i += 1
        out.append("<p>%s</p>" % inline(" ".join(para)))
    return "\n".join(out)


def render_list(block):
    """Nested lists by indentation. Indented non-item lines continue the previous item."""
    html_out, stack = [], []  # stack of (indent, tag)
    for line in block:
        m = ITEM.match(line)
        if not m:
            html_out.append(" " + inline(line.strip()))
            continue
        indent, marker, text = len(m.group(1)), m.group(2), m.group(3)
        tag = "ol" if marker[0].isdigit() else "ul"
        while stack and indent < stack[-1][0]:
            html_out.append("</li></%s>" % stack.pop()[1])
        if stack and indent == stack[-1][0]:
            if stack[-1][1] != tag:
                html_out.append("</li></%s>" % stack.pop()[1])
                html_out.append("<%s><li>" % tag)
                stack.append((indent, tag))
            else:
                html_out.append("</li><li>")
        else:
            html_out.append("<%s><li>" % tag)
            stack.append((indent, tag))
        html_out.append(inline(text))
    while stack:
        html_out.append("</li></%s>" % stack.pop()[1])
    return "".join(html_out)


# ---- Split into title, intro and chapters ----
lines = MD.splitlines()
title = lines[0].lstrip("# ").strip()
draft_line = lines[2].strip() if len(lines) > 2 else ""
sections, cur = [], None
for line in lines[3:]:
    if line.startswith("## "):
        cur = {"title": line[3:].strip(), "lines": []}
        sections.append(cur)
    elif cur is not None:
        cur["lines"].append(line)

toc, body = [], []
for sec in sections:
    m = re.match(r"^(\d+)\.\s*(.*)$", sec["title"])
    num, name = (m.group(1), m.group(2)) if m else ("·", sec["title"])
    sid = "s" + num if m else "src"
    toc.append((sid, num, name))
    parts, chunk = [], []
    for line in sec["lines"]:
        if line.startswith("### "):
            parts.append(render_block(chunk))
            chunk = []
            parts.append("<h3>%s</h3>" % inline(line[4:].strip()))
        else:
            chunk.append(line)
    parts.append(render_block(chunk))
    n_html = '<span class="n">%s</span>' % num if m else ""
    body.append('<section id="%s">\n<h2>%s%s</h2>\n%s\n</section>' % (sid, n_html, inline(name), "\n".join(p for p in parts if p)))

nav = "\n".join('<li><a href="#%s"><span>%s</span>%s</a></li>' % (sid, num, inline(name)) for sid, num, name in toc)
mnav = "\n".join('<li><a href="#%s">%s</a></li>' % (sid, inline(name)) for sid, num, name in toc)

page = """<title>מטה רכב לקלפי</title>
%s
%s

<div class="wrap">
  <header class="top">
    <div class="eyebrow">אפיון מערכת · %s</div>
    <h1>%s</h1>
    <p class="lede">מערכת למוקד טלפוני שקולט פניות של מצביעים שצריכים הסעה, מסדר אותן בתור ומשבץ נהגים מתנדבים, עם קיבוץ נוסעים לפי מקום איסוף ואשכול קלפיות.</p>
    <div class="meta"><span class="chip">כנסת ורשויות מקומיות</span><span class="chip">עד 1000 פניות לעיר ביום</span></div>
  </header>

  <details class="toc-mobile">
    <summary>תוכן העניינים</summary>
    <ol>
%s
    </ol>
  </details>

  <div class="layout">
    <nav class="toc" aria-label="תוכן העניינים">
      <div class="label">פרקים</div>
      <ol>
%s
      </ol>
    </nav>
    <main>
%s
    </main>
  </div>
</div>
""" % (FONTS, STYLE, html.escape(draft_line), html.escape(title.replace("אפיון: ", "")), mnav, nav, "\n".join(body))

(HERE / "spec.html").write_text(page, encoding="utf-8")
print("spec.html built:", len(sections), "chapters")
