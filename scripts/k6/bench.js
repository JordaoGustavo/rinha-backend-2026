import http from 'k6/http';
import { check } from 'k6';

const API_URL  = __ENV.API_URL  || 'http://localhost:9999';
const VUS      = parseInt(__ENV.VUS      || '20');
const DURATION = __ENV.DURATION || '60s';

const URL = `${API_URL}/fraud-score`;
const HEADERS = { 'Content-Type': 'application/json' };

const PAYLOAD = JSON.stringify({
    id: 'k6-bench-tx',
    transaction: { amount: 250.00, installments: 1, requested_at: '2026-04-15T13:42:11Z' },
    customer:    { avg_amount: 180.00, tx_count_24h: 3, known_merchants: ['m-001', 'm-002'] },
    merchant:    { id: 'm-001', mcc: 5411, avg_amount: 200.00 },
    terminal:    { is_online: true, card_present: true, km_from_home: 2.5 },
    last_transaction: { timestamp: '2026-04-15T11:10:00Z', km_from_current: 1.8 },
});

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
    const res = http.post(URL, PAYLOAD, { headers: HEADERS });
    check(res, { 'status 200': (r) => r.status === 200 });
}
