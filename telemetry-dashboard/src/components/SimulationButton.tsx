/**
 * SimulationButton.tsx
 *
 * Start / Stop simulation control for the HUD bar.
 *
 * State machine:
 *   STOPPED  → user clicks → LOADING → POST /api/simulation/start → RUNNING
 *   RUNNING  → user clicks → LOADING → POST /api/simulation/stop  → STOPPED
 *   LOADING  → button is disabled (prevents double-click)
 *
 * On mount the component calls GET /api/simulation/status to sync with
 * whatever state the backend is in (handles page refresh mid-session).
 *
 * Performance note (as required):
 *   Button state only changes on:
 *     - mount (one status GET)
 *     - user click (one POST)
 *   It is NEVER updated by incoming telemetry packets.
 */

import { useEffect, useState } from 'react';
import { Play, Square, Loader2 } from 'lucide-react';

// ── Types ─────────────────────────────────────────────────────────────────────

type SimState = 'stopped' | 'running' | 'loading';

interface StatusResponse {
    isRunning: boolean;
}

// ── Constants ─────────────────────────────────────────────────────────────────

const BASE_URL = 'http://localhost:5000/api/simulation';

// ── Helper ────────────────────────────────────────────────────────────────────

async function callApi(path: string, method: 'GET' | 'POST'): Promise<Response> {
    return fetch(`${BASE_URL}${path}`, {
        method,
        headers: { 'Content-Type': 'application/json' },
    });
}

// ── Component ─────────────────────────────────────────────────────────────────

export function SimulationButton() {
    const [simState, setSimState] = useState<SimState>('stopped');

    // ── Sync with backend on mount ────────────────────────────────────────────
    useEffect(() => {
        callApi('/status', 'GET')
            .then(res => res.json())
            .then((data: StatusResponse) => {
                setSimState(data.isRunning ? 'running' : 'stopped');
            })
            .catch(() => {
                // Backend may not be up yet — silently stay at 'stopped'
            });
    }, []);

    // ── Click handler ─────────────────────────────────────────────────────────
    const handleClick = async () => {
        if (simState === 'loading') return;   // guard against rapid clicks

        const action = simState === 'stopped' ? '/start' : '/stop';
        setSimState('loading');

        try {
            const res = await callApi(action, 'POST');

            if (res.ok) {
                const data: StatusResponse = await res.json();
                setSimState(data.isRunning ? 'running' : 'stopped');
            } else if (res.status === 409) {
                // Backend says already in desired state — re-sync
                const status = await callApi('/status', 'GET');
                const data: StatusResponse = await status.json();
                setSimState(data.isRunning ? 'running' : 'stopped');
            } else {
                // Unexpected error — revert to previous state
                setSimState(action === '/start' ? 'stopped' : 'running');
                console.error(`Simulation ${action} returned HTTP ${res.status}`);
            }
        } catch (err) {
            // Network error — revert
            setSimState(action === '/start' ? 'stopped' : 'running');
            console.error('Simulation API call failed:', err);
        }
    };

    // ── Visual config per state ───────────────────────────────────────────────
    const config = {
        stopped: {
            label:    'Start Simulation',
            icon:     <Play size={13} />,
            bg:       '#16a34a',   // green-600
            bgHover:  '#15803d',   // green-700
            border:   '#15803d',
            glow:     'rgba(22,163,74,0.3)',
        },
        running: {
            label:    'Stop Simulation',
            icon:     <Square size={13} />,
            bg:       '#dc2626',   // red-600
            bgHover:  '#b91c1c',   // red-700
            border:   '#b91c1c',
            glow:     'rgba(220,38,38,0.3)',
        },
        loading: {
            label:    'Please wait…',
            icon:     <Loader2 size={13} className="animate-spin" />,
            bg:       '#374151',   // gray-700
            bgHover:  '#374151',
            border:   '#4b5563',
            glow:     'transparent',
        },
    } as const;

    const { label, icon, bg, border, glow } = config[simState];

    return (
        <button
            onClick={handleClick}
            disabled={simState === 'loading'}
            title={label}
            aria-label={label}
            className="flex items-center gap-1.5 px-3 py-1.5 rounded text-xs font-mono font-semibold transition-all duration-150 select-none"
            style={{
                background:    bg,
                border:        `1px solid ${border}`,
                color:         '#fff',
                opacity:       simState === 'loading' ? 0.7 : 1,
                cursor:        simState === 'loading' ? 'not-allowed' : 'pointer',
                boxShadow:     `0 0 8px ${glow}`,
                letterSpacing: '0.05em',
            }}
        >
            {icon}
            <span>{label}</span>
        </button>
    );
}
