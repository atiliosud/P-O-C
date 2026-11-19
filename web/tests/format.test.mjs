// Display rules of the reviewer app. Run with `npm test` (esbuild bundles the TypeScript, node:test runs it).
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { auditProposedText, formatTime, isSetAside, notApplicableText, showDependsFlag, splitAtQuote } from '../src/format.ts';

const q4Yes = { questionId: 'Q4', value: 'Yes' };

test('a not applicable dependent shows the parent real final value and the rule', () => {
  assert.equal(notApplicableText(q4Yes, 'No'), 'Not applicable: Q4 is No (applies only if Q4 = Yes)');
});

test('a not applicable dependent whose parent has no final value says the parent does not apply', () => {
  assert.equal(notApplicableText(q4Yes, undefined), 'Not applicable: Q4 does not apply (applies only if Q4 = Yes)');
  assert.equal(notApplicableText(undefined, undefined), 'Not applicable');
});

test('the depends flag shows only while the parent is unconfirmed', () => {
  assert.equal(showDependsFlag(true, 'ParentUnconfirmed'), true);
  assert.equal(showDependsFlag(true, 'NotApplicable'), false); // withdrawn after Q4 = No
  assert.equal(showDependsFlag(true, 'Applicable'), false); // Q4 confirmed Yes
  assert.equal(showDependsFlag(false, 'ParentUnconfirmed'), false);
  assert.equal(showDependsFlag(undefined, 'ParentUnconfirmed'), false);
});

test('a final value is set aside only when its question no longer applies', () => {
  assert.equal(isSetAside(true, 'NotApplicable'), true); // Q4.1 answered, then Q4 corrected to No
  assert.equal(isSetAside(true, 'Applicable'), false);
  assert.equal(isSetAside(false, 'NotApplicable'), false);
});

test('the audit proposed cell names the agent once', () => {
  const text = auditProposedText({ proposedValue: 'Yes', proposedBy: 'Answerer agent', promptVersion: 'v12' });
  assert.equal(text, 'Yes · Answerer agent · prompt v12');
  assert.equal(auditProposedText({ proposalStatus: 'NeedsAnalyst', proposedBy: 'Answerer agent', promptVersion: 'v12' }), 'NeedsAnalyst · Answerer agent · prompt v12');
});

test('times are shown in UTC as "17 Nov 10:42"', () => {
  assert.equal(formatTime('2026-11-17T10:42:00Z'), '17 Nov 10:42');
});

test('the quote is found across different line breaks', () => {
  const page = 'Account status\nThe account held with Example Bank\n(Zurich) was closed on 14 March 2024.';
  const parts = splitAtQuote(page, 'The account held with Example Bank (Zurich) was closed on 14 March 2024.');
  assert.ok(parts);
  assert.equal(parts[0], 'Account status\n');
  assert.equal(parts[2], '');
});
