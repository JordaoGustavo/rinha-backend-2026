import http from 'k6/http';
import { check } from 'k6';
import { Trend } from 'k6/metrics';

const API_URL  = __ENV.API_URL  || 'http://localhost:9999';
const VUS      = parseInt(__ENV.VUS      || '20');
const DURATION = __ENV.DURATION || '60s';

const URL = `${API_URL}/fraud-score`;
const HEADERS = { 'Content-Type': 'application/json' };

// Pre-generate a corpus of varied payloads. Building JSON in the hot path is
// expensive enough on k6 side to skew measurements, so we bake them once.
const PAYLOADS = (function () {
    const amounts        = [10, 50, 100, 250, 500, 1000, 2500, 5000, 10000, 50000];
    const installments   = [1, 3, 6, 12];
    const mccs           = [5411, 5812, 5814, 5912, 5921, 5942, 6011, 7011, 7995, 4121];
    const kmFromHome     = [0.2, 1.5, 5.0, 25.0, 200.0];
    const txCount24h     = [0, 1, 3, 10, 50];
    const merchantList   = [
        [],
        ['m-001'],
        ['m-001', 'm-002'],
        ['m-001', 'm-002', 'm-003', 'm-004', 'm-005'],
    ];
    const terminalFlags  = [
        { is_online: true,  card_present: true  },
        { is_online: true,  card_present: false },
        { is_online: false, card_present: true  },
        { is_online: false, card_present: false },
    ];

    const out = [];
    let i = 0;
    for (const a of amounts) {
        for (const m of mccs) {
            for (const k of kmFromHome) {
                const inst = installments[i % installments.length];
                const tx24 = txCount24h[i % txCount24h.length];
                const merch = merchantList[i % merchantList.length];
                const flags = terminalFlags[i % terminalFlags.length];
                i++;
                out.push(JSON.stringify({
                    id: `k6-bench-tx-${i}`,
                    transaction:      { amount: a,        installments: inst, requested_at: '2026-04-15T13:42:11Z' },
                    customer:         { avg_amount: a / 1.5, tx_count_24h: tx24, known_merchants: merch },
                    merchant:         { id: 'm-001',  mcc: m, avg_amount: a * 0.9 },
                    terminal:         { ...flags, km_from_home: k },
                    last_transaction: { timestamp: '2026-04-15T11:10:00Z', km_from_current: k / 2 },
                }));
            }
        }
    }
    return out;
})();

// Per-stage metrics — populated only if the API emits Server-Timing.
const tParse  = new Trend('s_parse_us',  true);
const tCent   = new Trend('s_centroid_us', true);
const tScan   = new Trend('s_scan_us',     true);
const tBbox   = new Trend('s_bbox_us',     true);
const tRerank = new Trend('s_rerank_us',   true);
const tTotal  = new Trend('s_total_us',    true);

function parseServerTiming(header) {
    if (!header) return;
    // Format: "parse;dur=12.34, s1-cent;dur=80.5, s1-scan;dur=300.0, s1-bbox;dur=1000.0, s2-rerank;dur=20.0, total;dur=1500.0"
    // dur is in microseconds (we emit µs from the server).
    const parts = header.split(',');
    for (const p of parts) {
        const [name, durPart] = p.trim().split(';');
        if (!durPart) continue;
        const dur = parseFloat(durPart.replace(/^dur=/, ''));
        if (isNaN(dur)) continue;
        switch (name) {
            case 'parse':     tParse.add(dur); break;
            case 's1-cent':   tCent.add(dur); break;
            case 's1-scan':   tScan.add(dur); break;
            case 's1-bbox':   tBbox.add(dur); break;
            case 's2-rerank': tRerank.add(dur); break;
            case 'total':     tTotal.add(dur); break;
        }
    }
}

export const options = {
    discardResponseBodies: true,
    scenarios: {
        sustained: {
            executor: 'ramping-vus',
            startVUs: 0,
            stages: [
                { duration: '5s',     target: VUS },
                { duration: DURATION, target: VUS },
                { duration: '2s',     target: 0   },
            ],
            gracefulRampDown: '5s',
        },
    },
    summaryTrendStats: ['avg', 'min', 'med', 'p(95)', 'p(99)', 'p(99.9)', 'max'],
};

export default function () {
    const idx = Math.floor(Math.random() * PAYLOADS.length);
    const res = http.post(URL, PAYLOADS[idx], { headers: HEADERS });
    check(res, { 'status 200': (r) => r.status === 200 });
    parseServerTiming(res.headers['Server-Timing']);
}
