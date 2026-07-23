/**
 * warningStore.ts
 *
 * Pure-module, zero-React alert store.
 *
 * Architecture:
 *  - Subscribes to signalRService.onAlert() at module load time (once).
 *  - Stores warnings in a plain Map<sensorId, AlertFrame[]> — no React state,
 *    no re-renders on packet arrival.
 *  - Consumers read via getWarnings() / getWarningCount() (pull model).
 *  - StreamingChart polls getWarningCount() every 500 ms via setInterval to
 *    update the badge — the only allowed periodic state update.
 *
 * CONSTRAINTS ENFORCED:
 *  - signalr.ts is NOT modified.
 *  - App.tsx telemetry listener is NOT touched.
 *  - No React imports anywhere in this file.
 */

import { signalRService } from './signalr';
import type { AlertFrame } from './signalr';

// ── Constants ────────────────────────────────────────────────────────────────

/** Maximum alerts stored per sensor. Oldest are dropped when exceeded. */
const MAX_PER_SENSOR = 1_000;

// ── Module-level storage (not React state) ────────────────────────────────────

/**
 * Map<sensorId → AlertFrame[]> — newest first.
 * Initialised lazily as sensors emit alerts.
 */
const store = new Map<number, AlertFrame[]>();

// ── Subscription (runs once at module import time) ───────────────────────────

signalRService.onAlert((alert: AlertFrame) => {
    // Skip severity 0 (Normal) — we only track real warnings/criticals
    if (alert.severity === 0) return;

    let list = store.get(alert.sensorId);
    if (!list) {
        list = [];
        store.set(alert.sensorId, list);
    }

    // Prepend so index 0 is always the newest
    list.unshift(alert);

    // Cap at max — drop the oldest (tail)
    if (list.length > MAX_PER_SENSOR) {
        list.length = MAX_PER_SENSOR;
    }
});

// ── Public API ────────────────────────────────────────────────────────────────

/**
 * Returns a SHALLOW COPY of all warnings for a sensor, newest first.
 * Callers should treat the returned array as read-only.
 */
export function getWarnings(sensorId: number): AlertFrame[] {
    return store.get(sensorId) ?? [];
}

/**
 * Cheap O(1) count — safe to call inside a 500 ms setInterval.
 */
export function getWarningCount(sensorId: number): number {
    return store.get(sensorId)?.length ?? 0;
}

// Re-export AlertFrame so consumers only need one import
export type { AlertFrame };
