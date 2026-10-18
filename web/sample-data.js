// browser-only sample messages, loaded by bridge.js when there is no WebView2 host.
// the host never loads this file
(function () {
  'use strict';

  const MIN = 60000;

  // live reads of 2026-10-17 4:43 PM CDT and the agy report of 2026-10-18. times are kept
  // relative to the read so the countdowns read the same on any day. ?scenario=stale swaps
  // in the Anthropic provider as a stale reading from nine hours ago
  function usage() {
    const now = Date.now();
    const iso = ms => new Date(ms).toISOString();
    const at = mins => iso(now + mins * MIN);
    // [minutes from the read, percent used] pairs, oldest first, the read itself last
    const hist = (read, pairs) => pairs.map(([mins, used]) => ({ at: iso(read + mins * MIN), usedPercent: used }));
    const stale = /(?:^|[?&])scenario=stale(?:&|$)/.test(location.search);
    return {
      type: 'snapshot',
      busy: false,
      snapshot: {
        updatedAt: iso(now),
        providers: [stale ? anthropicStale(now, iso, hist) : anthropic(now, iso, at, hist), openai(now, iso, at), google(now, iso, at, hist)]
      }
    };
  }

  // claude in 2 h 17 m; the weekly ran ahead of even pace early in the week, then slowed
  function anthropic(now, iso, at, hist) {
    const claudeReset = at(137);
    return {
      provider: 'Anthropic',
      plan: 'Claude Max 20x',
      state: 'ok',
      message: null,
      detail: null,
      meters: [
        { key: 'session', name: 'Session', windowMinutes: 300, usedPercent: 34,
          resetsAt: claudeReset, severity: 'normal', note: null },
        { key: 'weekly', name: 'Weekly, all models', windowMinutes: 10080, usedPercent: 80,
          resetsAt: claudeReset, severity: 'warning', note: null,
          history: hist(now, [[-9300, 9], [-7400, 30], [-5800, 47], [-4300, 58], [-2900, 64],
            [-1500, 71], [-480, 77], [0, 80]]) },
        { key: 'weekly_fable', name: 'Weekly, Fable only', windowMinutes: 10080, usedPercent: 70,
          resetsAt: claudeReset, severity: 'normal', note: 'headroom 15% of weekly' }
      ],
      footnote: 'Extra usage on · $0.00 of $200.00 used',
      actions: [],
      updatedAt: iso(now)
    };
  }

  // the last good read, nine hours old. its session window has since reset, so the page
  // draws it as a finished ghost; its weeklies run 3 d 2 h on
  function anthropicStale(now, iso, hist) {
    const read = now - 9 * 60 * MIN;
    const from = mins => iso(read + mins * MIN);
    return {
      provider: 'Anthropic',
      plan: 'Claude Max 20x',
      state: 'stale',
      message: 'Claude Code token expired; the refresh ladder did not replace it',
      detail: null,
      meters: [
        { key: 'session', name: 'Session', windowMinutes: 300, usedPercent: 34,
          resetsAt: from(137), severity: 'normal', note: null, history: [] },
        { key: 'weekly', name: 'Weekly, all models', windowMinutes: 10080, usedPercent: 48,
          resetsAt: from(4442), severity: 'normal', note: null,
          history: hist(read, [[-5000, 7], [-3600, 19], [-2200, 33], [-900, 42], [0, 48]]) },
        { key: 'weekly_fable', name: 'Weekly, Fable only', windowMinutes: 10080, usedPercent: 41,
          resetsAt: from(4442), severity: 'normal', note: 'headroom 29.5% of weekly', history: [] }
      ],
      footnote: 'Extra usage on · $0.00 of $200.00 used',
      actions: [{ id: 'claude-refresh', label: 'Refresh via Claude Code' }],
      updatedAt: iso(read)
    };
  }

  // codex in 6 d 23 h 25 m; weekly only, so the page shows the 5-hour ghost
  function openai(now, iso, at) {
    return {
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
      updatedAt: iso(now)
    };
  }

  // the five buckets of tests/fixtures/agy-usage-2026-10-18.json in report order. an idle
  // bucket resets one window from now; argon keeps the fixture's 1.01% and its reset 3.5 min
  // short of a week. gemini last ran 7 h ago, claude and gpt over the last 85 min
  function google(now, iso, at, hist) {
    return {
      provider: 'Google',
      plan: 'Antigravity CLI',
      state: 'ok',
      message: null,
      detail: null,
      meters: [
        { key: 'gemini-weekly', name: 'Gemini Models', windowMinutes: 10080, usedPercent: 37,
          resetsAt: at(10080 - 3120), severity: 'normal', note: null,
          history: hist(now, [[-2400, 8], [-1320, 19], [-420, 37], [0, 37]]) },
        { key: 'gemini-5h', name: 'Gemini Models', windowMinutes: 300, usedPercent: 0,
          resetsAt: at(300), severity: 'normal', note: null, history: hist(now, [[0, 0]]) },
        { key: '3p-weekly', name: 'Claude and GPT models', windowMinutes: 10080, usedPercent: 9,
          resetsAt: at(10080 - 100), severity: 'normal', note: null,
          history: hist(now, [[-85, 3], [-40, 6], [0, 9]]) },
        { key: '3p-5h', name: 'Claude and GPT models', windowMinutes: 300, usedPercent: 62,
          resetsAt: at(300 - 100), severity: 'normal', note: null,
          history: hist(now, [[-85, 18], [-40, 45], [0, 62]]) },
        { key: 'gemini-argon-wtu-weekly', name: 'Gemini 4 Argon', windowMinutes: 10080, usedPercent: 1.01,
          resetsAt: at(10080 - 3.5), severity: 'normal', note: null, history: hist(now, [[0, 1.01]]) }
      ],
      footnote: null,
      actions: [],
      updatedAt: iso(now)
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
