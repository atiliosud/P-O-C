const timeFormat = new Intl.DateTimeFormat('en-GB', {
  day: 'numeric',
  month: 'short',
  hour: '2-digit',
  minute: '2-digit',
  hourCycle: 'h23',
  timeZone: 'UTC',
});

// "17 Nov 10:42" in UTC.
export const formatTime = (iso: string): string => timeFormat.format(new Date(iso)).replace(',', '');

export const errorText = (e: unknown): string => (e instanceof Error ? e.message : String(e));

// "Not applicable: Q4 is No (applies only if Q4 = Yes)": the parent's real final value, then the rule.
export function notApplicableText(dependsOn: { questionId: string; value: string } | undefined, parentFinal: string | undefined): string {
  if (!dependsOn) return 'Not applicable';
  const rule = `applies only if ${dependsOn.questionId} = ${dependsOn.value}`;
  return parentFinal
    ? `Not applicable: ${dependsOn.questionId} is ${parentFinal} (${rule})`
    : `Not applicable: ${dependsOn.questionId} does not apply (${rule})`;
}

// A final value on a question that no longer applies (Q4.1 after Q4 moved to No) is kept, not deleted, but set aside:
// it does not count as answered and applies again if the parent changes back.
export const isSetAside = (hasFinal: boolean, applicability: string): boolean => hasFinal && applicability === 'NotApplicable';

// "Depends on Q4, not confirmed yet" only while the parent really has no final answer (not on withdrawn or confirmed dependents).
export const showDependsFlag = (dependsOnUnconfirmed: boolean | undefined, applicability: string): boolean =>
  !!dependsOnUnconfirmed && applicability === 'ParentUnconfirmed';

// Audit "Proposed" cell: "Yes · Answerer agent · prompt v12". The server already appends " agent" to the agent name.
export const auditProposedText = (r: { proposedValue?: string; proposalStatus?: string; proposedBy?: string; promptVersion?: string }): string =>
  `${r.proposedValue ?? r.proposalStatus ?? ''} · ${r.proposedBy ?? ''} · prompt ${r.promptVersion ?? ''}`;

const squash = (s: string) => s.replace(/\s+/g, ' ').trim();

// Splits a page into [before, match, after] around the quote, ignoring whitespace differences.
export function splitAtQuote(text: string, quote: string): [string, string, string] | null {
  const wanted = squash(quote);
  if (!wanted) return null;
  let squashed = '';
  const starts: number[] = []; // original index of each squashed character
  let lastWasSpace = true;
  for (let i = 0; i < text.length; i++) {
    const isSpace = /\s/.test(text[i]);
    if (isSpace && lastWasSpace) continue;
    squashed += isSpace ? ' ' : text[i];
    starts.push(i);
    lastWasSpace = isSpace;
  }
  const at = squashed.indexOf(wanted);
  if (at < 0) return null;
  const from = starts[at];
  const to = starts[at + wanted.length - 1] + 1;
  return [text.slice(0, from), text.slice(from, to), text.slice(to)];
}
