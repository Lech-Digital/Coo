// Verifies every character count claimed in store-assets/PLAY-LISTING.md.
//   node store-assets/_src/check-listing.mjs
//
// A field is a heading of the form  "### <name> — <count> / <limit>"  followed by a fenced block.
// Counts are Unicode code points ([...text].length), the way Play Console counts: "–", "·", "ł"
// and "ą" are one character each. Trailing newline of the block is not counted.
//
// Fields belong to the locale of the "## N. <Language> (<locale>) → …" section they sit in. Every
// locale in LOCALES must have an app name (/30), a short description (/80) and a full description
// (/4,000). App names (all /30 fields, alternates included) must not carry promotional terms,
// which Play's metadata policy forbids in the title. Competitor apps may not be named anywhere.
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const LOCALES = ['en-US', 'pl-PL', 'de-DE', 'fr-FR', 'es-ES', 'it-IT', 'nl-NL'];
const REQUIRED_LIMITS = [30, 80, 4000];
// Promotional terms not allowed in the app title, in every listing language.
const TITLE_BANNED = /\b(free|ad-?free|no ads|best|top|#1|new|sale|gratis|kostenlos|werbefrei|ohne werbung|gratuit|sans pub|gratuito|sin anuncios|senza pubblicità|zonder reclame|reclamevrij|darmow\w*|bez reklam)\b|#1/iu;
const OTHER_APPS = /\b(huckleberry|sprout\s*baby|nara\s*baby|glow\s*baby|ovia\s*parenting|cubtale|feed\s*baby)\b/i;

const file = join(dirname(fileURLToPath(import.meta.url)), '..', 'PLAY-LISTING.md');
const md = readFileSync(file, 'utf8');
const section = /^## \d+\. .+? \(([a-z]{2}-[A-Z]{2})\)/gm;
const re = /^#{2,4} (.+?) — ([\d,]+) \/ ([\d,]+)\s*\n+```[a-z]*\n([\s\S]*?)\n```/gm;
const num = (s) => Number(s.replace(/,/g, ''));

const sections = [...md.matchAll(section)].map((m) => ({ locale: m[1], at: m.index }));
const localeAt = (i) => sections.filter((s) => s.at <= i).at(-1)?.locale ?? '?';
const seen = Object.fromEntries(LOCALES.map((l) => [l, new Set()]));

let fields = 0, bad = 0;
for (const m of md.matchAll(re)) {
  fields++;
  const [, name, claimed, limit, text] = m;
  const locale = localeAt(m.index);
  const n = [...text].length;
  const problems = [];
  if (n !== num(claimed)) problems.push(`claimed ${claimed} but is ${n}`);
  if (n > num(limit)) problems.push(`over limit ${limit}`);
  if (num(limit) === 30 && TITLE_BANNED.test(text)) problems.push('promotional term in title');
  if (OTHER_APPS.test(text)) problems.push('names another app');
  if (seen[locale]) seen[locale].add(num(limit));
  if (problems.length) bad++;
  console.log(`${problems.length ? 'BAD' : 'OK '} ${locale.padEnd(5)} ${String(n).padStart(5)} / ${limit.padEnd(5)} ${name}${problems.length ? '  <- ' + problems.join(', ') : ''}`);
}
if (fields === 0) { console.error('no fields found'); process.exit(1); }

for (const l of LOCALES) {
  const missing = REQUIRED_LIMITS.filter((lim) => !seen[l].has(lim));
  if (missing.length) { bad++; console.log(`BAD ${l}: missing field(s) with limit ${missing.join(', ')}`); }
}
if (OTHER_APPS.test(md)) { bad++; console.log('BAD listing file names a competitor app'); }

console.log(bad ? `\nFAILED: ${bad} problem(s)` : `\nSUCCESS: all ${fields} fields in ${LOCALES.length} languages match and are within limits`);
process.exit(bad ? 1 : 0);
