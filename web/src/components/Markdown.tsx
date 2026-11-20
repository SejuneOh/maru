"use client";

import { Fragment, type ReactNode } from "react";
import { safeHttpUrl } from "@/lib/labels";

/**
 * A tiny, dependency-free Markdown renderer that returns React nodes only, never raw HTML.
 * Supports headings, ordered/unordered lists, bold, italic, links, inline and block code,
 * and blockquotes. Anything it does not recognise, including raw HTML tags, shows as plain
 * text (React escapes it), so there is no injection surface. Links are limited to safe http(s).
 */
export function Markdown({ text, compact = false }: { text: string; compact?: boolean }) {
  const blocks = parseBlocks(text);
  if (blocks.length === 0) return null;
  return <div className={compact ? "text-ui" : ""}>{blocks.map((b, i) => renderBlock(b, i, compact))}</div>;
}

type Block =
  | { kind: "heading"; level: number; text: string }
  | { kind: "ul"; items: string[] }
  | { kind: "ol"; items: string[] }
  | { kind: "quote"; text: string }
  | { kind: "code"; text: string }
  | { kind: "p"; text: string };

function parseBlocks(src: string): Block[] {
  const lines = src.replace(/\r\n?/g, "\n").split("\n");
  const blocks: Block[] = [];
  let i = 0;
  while (i < lines.length) {
    const line = lines[i];
    if (line.trim() === "") {
      i++;
      continue;
    }
    // Fenced code block: ``` ... ```
    if (/^\s*```/.test(line)) {
      const body: string[] = [];
      i++;
      while (i < lines.length && !/^\s*```/.test(lines[i])) body.push(lines[i++]);
      i++; // consume the closing fence (or run off the end)
      blocks.push({ kind: "code", text: body.join("\n") });
      continue;
    }
    // Heading: #..###### followed by a space ([^\n] so a stray line separator in the text can't void the match)
    const h = /^(#{1,6})\s+([^\n]*)$/.exec(line);
    if (h) {
      blocks.push({ kind: "heading", level: h[1].length, text: h[2].trim() });
      i++;
      continue;
    }
    // Blockquote: one or more lines starting with >
    if (/^\s*>\s?/.test(line)) {
      const body: string[] = [];
      while (i < lines.length && /^\s*>\s?/.test(lines[i])) body.push(lines[i++].replace(/^\s*>\s?/, ""));
      blocks.push({ kind: "quote", text: body.join("\n") });
      continue;
    }
    // Unordered list
    if (/^\s*[-*+]\s+/.test(line)) {
      const items: string[] = [];
      while (i < lines.length && /^\s*[-*+]\s+/.test(lines[i])) items.push(lines[i++].replace(/^\s*[-*+]\s+/, ""));
      blocks.push({ kind: "ul", items });
      continue;
    }
    // Ordered list
    if (/^\s*\d+[.)]\s+/.test(line)) {
      const items: string[] = [];
      while (i < lines.length && /^\s*\d+[.)]\s+/.test(lines[i])) items.push(lines[i++].replace(/^\s*\d+[.)]\s+/, ""));
      blocks.push({ kind: "ol", items });
      continue;
    }
    // Paragraph: consume this line (it started no other block), then gather until a blank line or a new block.
    // Taking the first line unconditionally guarantees i advances, so the outer loop can never stall.
    const body: string[] = [lines[i++]];
    while (
      i < lines.length &&
      lines[i].trim() !== "" &&
      !/^\s*```/.test(lines[i]) &&
      !/^(#{1,6})\s+/.test(lines[i]) &&
      !/^\s*>\s?/.test(lines[i]) &&
      !/^\s*[-*+]\s+/.test(lines[i]) &&
      !/^\s*\d+[.)]\s+/.test(lines[i])
    ) {
      body.push(lines[i++]);
    }
    blocks.push({ kind: "p", text: body.join("\n") });
  }
  return blocks;
}

function renderBlock(b: Block, key: number, compact: boolean): ReactNode {
  const first = key === 0;
  switch (b.kind) {
    case "heading": {
      const cls = b.level <= 3
        ? `${first ? "" : "mt-6 "}mb-2 text-heading font-semibold text-ink`
        : `${first ? "" : "mt-4 "}mb-1 text-ui font-semibold text-ink`;
      const Tag = (`h${Math.min(b.level + 2, 6)}` as "h3" | "h4" | "h5" | "h6");
      return <Tag key={key} className={cls}>{renderInline(b.text)}</Tag>;
    }
    case "ul":
      return (
        <ul key={key} className={`${first ? "" : "mt-3 "}mb-3 list-disc space-y-1 pl-5 ${compact ? "text-ui" : "text-body"} text-ink-soft`}>
          {b.items.map((it, j) => <li key={j}>{renderInline(it)}</li>)}
        </ul>
      );
    case "ol":
      return (
        <ol key={key} className={`${first ? "" : "mt-3 "}mb-3 list-decimal space-y-1 pl-5 ${compact ? "text-ui" : "text-body"} text-ink-soft`}>
          {b.items.map((it, j) => <li key={j}>{renderInline(it)}</li>)}
        </ol>
      );
    case "quote":
      return (
        <blockquote key={key} className={`${first ? "" : "mt-3 "}mb-3 border-l-2 border-rule-strong pl-3 ${compact ? "text-ui" : "text-body"} text-muted`}>
          {renderInline(b.text)}
        </blockquote>
      );
    case "code":
      return (
        <pre key={key} className={`${first ? "" : "mt-3 "}mb-3 overflow-x-auto rounded-control bg-sunken p-3 text-small`}>
          <code className="font-mono text-ink-soft">{b.text}</code>
        </pre>
      );
    case "p":
      return (
        <p key={key} className={`${first ? "" : "mt-3 "}${compact ? "text-ui" : "text-body"} whitespace-pre-wrap text-ink-soft`}>
          {renderInline(b.text)}
        </p>
      );
  }
}

const LINK_CLS = "text-ink underline decoration-rule-strong underline-offset-2 hover:decoration-ink-soft";
const CODE_CLS = "rounded bg-sunken px-1 py-0.5 font-mono text-[0.85em] text-ink-soft";

/** Inline spans: `code`, [text](url), ![alt](url) as a link, **bold**, *italic*. Leftmost match wins. */
function renderInline(text: string, depth = 0): ReactNode[] {
  const out: ReactNode[] = [];
  let rest = text;
  let key = 0;
  const guard = depth > 4; // stop recursing into nested emphasis past a sane depth
  while (rest.length > 0) {
    const m = guard ? null : nextToken(rest);
    if (!m) {
      out.push(<Fragment key={key++}>{rest}</Fragment>);
      break;
    }
    if (m.index > 0) out.push(<Fragment key={key++}>{rest.slice(0, m.index)}</Fragment>);
    out.push(<Fragment key={key++}>{m.node(depth)}</Fragment>);
    rest = rest.slice(m.index + m.length);
  }
  return out;
}

type Token = { index: number; length: number; node: (depth: number) => ReactNode };

function nextToken(s: string): Token | null {
  const patterns: { re: RegExp; make: (mm: RegExpExecArray, depth: number) => ReactNode }[] = [
    { re: /`([^`]+)`/, make: (mm) => <code className={CODE_CLS}>{mm[1]}</code> },
    { re: /!\[([^\]]*)\]\(([^)\s]+)\)/, make: (mm) => linkNode(mm[2], mm[1] || mm[2]) },
    { re: /\[([^\]]+)\]\(([^)\s]+)\)/, make: (mm, d) => linkNode(mm[2], mm[1], d) },
    { re: /\*\*([^*]+)\*\*/, make: (mm, d) => <strong className="font-semibold text-ink">{renderInline(mm[1], d + 1)}</strong> },
    // Underscore emphasis only at word boundaries, so identifiers like user_id_field stay literal.
    { re: /(?<!\w)__([^_]+)__(?!\w)/, make: (mm, d) => <strong className="font-semibold text-ink">{renderInline(mm[1], d + 1)}</strong> },
    { re: /\*([^*]+)\*/, make: (mm, d) => <em>{renderInline(mm[1], d + 1)}</em> },
    { re: /(?<!\w)_([^_]+)_(?!\w)/, make: (mm, d) => <em>{renderInline(mm[1], d + 1)}</em> },
  ];
  let best: Token | null = null;
  for (const { re, make } of patterns) {
    const mm = re.exec(s);
    if (mm && (best === null || mm.index < best.index)) {
      best = { index: mm.index, length: mm[0].length, node: (d) => make(mm, d) };
    }
  }
  return best;
}

function linkNode(href: string, label: string, depth = 0): ReactNode {
  const safe = safeHttpUrl(href);
  if (!safe) return <>{label}</>;
  return (
    <a href={safe} target="_blank" rel="noopener noreferrer" className={LINK_CLS}>
      {renderInline(label, depth + 1)}
    </a>
  );
}
