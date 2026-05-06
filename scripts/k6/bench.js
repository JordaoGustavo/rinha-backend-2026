// k6 endpoint benchmark with Server-Timing capture.
//
// Reads /test-data.json (mounted from host) and POSTs random samples to the
// configured endpoint. Captures both client wall-clock (http_req_duration)
// and server compute time (parsed from Server-Timing header set by the API),
// so the difference attributes time to network / LB hops vs server work.
//
// ENV vars (with defaults):
//   ENDPOINT     — path to hit (default: /fraud-score)
//   API_URL      — base url   (default: http://localhost:9999)
//   VUS          — virtual users / concurrent connections (default: 10)
//   DURATION     — sustained duration after ramp-up (default: 30s)
//   RAMP_UP      — ramp-up time to reach VUS (default: 5s)
//   SAMPLE_LIMIT — how many distinct request bodies to keep in memory (default: 1000)

import http from 'k6/http';
import { check } from 'k6';
import { Trend } from 'k6/metrics';

const ENDPOINT     = __ENV.ENDPOINT     || '/fraud-score';
const API_URL      = __ENV.API_URL      || 'http://localhost:9999';
const VUS          = parseInt(__ENV.VUS         || '10');
const DURATION     = __ENV.DURATION     || '30s';
const RAMP_UP      = __ENV.RAMP_UP      || '5s';
const SAMPLE_LIMIT = parseInt(__ENV.SAMPLE_LIMIT || '1000');

// Load samples once at module init (k6 init phase, not VU phase — no per-iter cost).
const data = JSON.parse(open('/test-data.json'));
const samples = data.entries.slice(0, SAMPLE_LIMIT).map(e => JSON.stringify(e.request));

const serverCompute = new Trend('server_compute_ms', true);
const networkOverhead = new Trend('network_overhead_ms', true);

export const options = {
    discardResponseBodies: false, // we need headers
    scenarios: {
        sustained: {
            executor: 'ramping-vus',
            startVUs: 0,
            stages: [
                { duration: RAMP_UP,  target: VUS },
                { duration: DURATION, target: VUS },
                { duration: '2s',     target: 0   },
            ],
            gracefulRampDown: '5s',
        },
    },
    thresholds: {
        'http_req_duration': ['p(50)<5', 'p(99)<20'],
        'http_req_failed':   ['rate<0.001'],
    },
    summaryTrendStats: ['avg', 'min', 'med', 'p(95)', 'p(99)', 'p(99.9)', 'max'],
};

const HEADERS = { 'Content-Type': 'application/json' };
const URL = `${API_URL}${ENDPOINT}`;

export default function () {
    const body = samples[Math.floor(Math.random() * samples.length)];
    const res = http.post(URL, body, { headers: HEADERS });

    check(res, { 'status 200': (r) => r.status === 200 });

    const st = res.headers['Server-Timing'];
    if (st) {
        const m = /app;dur=([0-9.]+)/.exec(st);
        if (m) {
            const compute = parseFloat(m[1]);
            serverCompute.add(compute);
            networkOverhead.add(res.timings.duration - compute);
        }
    }
}

export function handleSummary(data) {
    // Concise stdout: focus on the metrics that matter for tail-latency tuning.
    const m = data.metrics;
    const fmt = (t) => t ? t.values : {};
    const dur = fmt(m.http_req_duration);
    const compute = fmt(m.server_compute_ms);
    const net = fmt(m.network_overhead_ms);
    const reqs = fmt(m.http_reqs);

    const lines = [
        '',
        `── ${ENDPOINT}  vus=${VUS}  duration=${DURATION} ──`,
        `  http_req_duration       p50=${dur.med?.toFixed(2)}ms  p95=${dur['p(95)']?.toFixed(2)}ms  p99=${dur['p(99)']?.toFixed(2)}ms  p99.9=${dur['p(99.9)']?.toFixed(2)}ms  max=${dur.max?.toFixed(2)}ms`,
        `  server_compute (header) p50=${compute.med?.toFixed(2)}ms  p95=${compute['p(95)']?.toFixed(2)}ms  p99=${compute['p(99)']?.toFixed(2)}ms  p99.9=${compute['p(99.9)']?.toFixed(2)}ms`,
        `  network_overhead        p50=${net.med?.toFixed(2)}ms  p95=${net['p(95)']?.toFixed(2)}ms  p99=${net['p(99)']?.toFixed(2)}ms`,
        `  throughput              ${reqs.rate?.toFixed(0)} r/s   total ${reqs.count}`,
        `  errors                  ${(fmt(m.http_req_failed).rate * 100)?.toFixed(3)}%`,
        '',
    ];
    return {
        stdout: lines.join('\n'),
    };
}
