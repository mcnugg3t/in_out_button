// browser-only sample messages, loaded by bridge.js when there is no WebView2 host.
// the host never loads this file
(function () {
  'use strict';

  const MIN = 60000;

  // live reads of 2026-10-17 4:43 PM CDT; resets are kept relative to now so the
  // countdowns read the same on any day: claude in 2 h 17 m, codex in 6 d 23 h 25 m
  function usage() {
    const now = Date.now();
    const at = mins => new Date(now + mins * MIN).toISOString();
    const claudeReset = at(137);
    return {
      type: 'snapshot',
      busy: false,
      snapshot: {
        updatedAt: new Date(now).toISOString(),
        providers: [
          {
            provider: 'Anthropic',
            plan: 'Claude Max 20x',
            state: 'ok',
            message: null,
            detail: null,
            meters: [
              { key: 'session', name: 'Session', windowMinutes: 300, usedPercent: 34,
                resetsAt: claudeReset, severity: 'normal', note: null },
              { key: 'weekly', name: 'Weekly, all models', windowMinutes: 10080, usedPercent: 80,
                resetsAt: claudeReset, severity: 'warning', note: null },
              { key: 'weekly_fable', name: 'Weekly, Fable only', windowMinutes: 10080, usedPercent: 70,
                resetsAt: claudeReset, severity: 'normal', note: 'headroom 15% of weekly' }
            ],
            footnote: 'Extra usage on · $0.00 of $200.00 used',
            actions: [],
            updatedAt: new Date(now).toISOString()
          },
          {
            provider: 'OpenAI',
            plan: 'ChatGPT plan via Codex',
            state: 'ok',
            message: null,
            detail: null,
            meters: [
              { key: 'codex_weekly', name: 'Weekly', windowMinutes: 10080, usedPercent: 6,
                resetsAt: at(6 * 1440 + 23 * 60 + 25), severity: 'normal', note: null }
            ],
            footnote: null,
            actions: [],
            updatedAt: new Date(now).toISOString()
          },
          {
            provider: 'Google',
            plan: 'Gemini AI Pro',
            state: 'unavailable',
            message: 'No local source on this machine.',
            detail: 'The Gemini CLI stopped serving AI Pro on 2026-06-18. The Antigravity CLI (agy) is not installed.',
            meters: [],
            footnote: null,
            actions: [
              { id: 'gemini-usage', label: 'Open usage page' },
              { id: 'antigravity-setup', label: 'Set up Antigravity' }
            ],
            updatedAt: new Date(now).toISOString()
          }
        ]
      }
    };
  }

  // the 13 illustrative tasks from candidate B; today is fixed so the due chips read the same
  function tasks() {
    const items = (pairs) => pairs.map(([text, done]) => ({ text, done }));
    const t = (id, title, bucket, progress, due, extra) => Object.assign({
      id, rev: 1, title, bucket, progress, priority: 'normal', checklist: [],
      created: '2026-10-12T15:00:00Z', updated: '2026-10-16T20:30:00Z'
    }, due ? { due } : {}, extra || {});
    return {
      type: 'state',
      today: '2026-10-17',
      file: 'persistent-memory\\tasks\\tasks.json',
      fallback: false,
      repos: ['capacity_estimation', 'in_out_button', 'persistent-memory',
        'rrl_proficiency_scraper', 'rrl_sikora', 'watertown_agronomy'],
      error: null,
      tasks: [
        t('a1c4e8f2b7d30965', 'Usage and tasks tabs: Astra plan review', 'projects', 'inProgress', '2026-10-18', {
          priority: 'important', repo: 'in_out_button',
          notes: 'Pick one candidate per tab first.\nThen send both specs to Astra.',
          checklist: items([['Write usage tab spec', true], ['Write tasks tab spec', true],
            ['Send both specs to Astra', false], ['Triage Astra findings', false],
            ['Commit the post-review spec', false]])
        }),
        t('8f3c2a1e9b7d4c6f', 'Refit buffer pH GAM with September data', 'projects', 'notStarted', '2026-10-24',
          { repo: 'rrl_sikora' }),
        t('3e9d1b7a6c2f8045', 'Fix rerun row merge in review grid', 'projects', 'complete', '2026-10-15', {
          repo: 'watertown_agronomy', completed: '2026-10-15T21:10:00Z',
          checklist: items([['Reproduce with a rerun sample', true], ['Merge rows on sample id', true],
            ['Add a regression test', true], ['Check the grid totals', true]])
        }),
        t('c7a05f3d91e2b684', 'Capacity plan 2027: staffing table', 'projects', 'notStarted', '2026-10-31',
          { repo: 'capacity_estimation' }),
        t('5b2e8d4c0a9f7136', 'Proficiency scraper: NAPT round 2026-3 import', 'projects', 'inProgress', '2026-10-21', {
          repo: 'rrl_proficiency_scraper',
          checklist: items([['Download the round 2026-3 report', true], ['Map analytes to lab codes', false],
            ['Import and spot-check', false]])
        }),
        t('e04f6a2b8d3c9157', 'October expense report', 'admin', 'notStarted', '2026-10-20', { priority: 'urgent' }),
        t('9d6c3e1f5a7b2048', 'Prep notes for Thursday one-on-one', 'admin', 'inProgress', '2026-10-23'),
        t('2a8f7c4e6b1d0935', 'Renew OneDrive app consent with IT', 'admin', 'notStarted', '2026-11-01'),
        t('f61b9e2d4c8a7303', 'Approve timesheets', 'admin', 'complete', '2026-10-17',
          { completed: '2026-10-17T14:05:00Z' }),
        t('4c3a7e9f2d6b1580', 'Order winter rye for food plots', 'misc', 'notStarted', '2026-10-22',
          { priority: 'important' }),
        t('b8e2d5a1f9c40776', 'Rebuild Debug exe after sign-in change', 'misc', 'complete', '2026-10-10',
          { completed: '2026-10-10T19:40:00Z' }),
        t('6f9a1c8e3b5d2047', 'Read Kugel, chapter 4', 'misc', 'notStarted', null),
        t('d25c8b6a4e1f9938', 'Truck oil change', 'misc', 'notStarted', '2026-10-15')
      ]
    };
  }

  window.InOutSample = { usage, tasks };
})();
