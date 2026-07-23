/**
 * StreamingChart.tsx
 *
 * ── CRITICAL PERFORMANCE CONTRACT ──────────────────────────────────────────
 * The uPlot chart renders at 60 fps via direct DOM mutation (setData).
 * NOTHING in this file may trigger a React re-render on every telemetry
 * packet. The two rules that enforce this:
 *
 *   1. All packet-driven updates go through refs + window events — never state.
 *   2. The ONLY periodic state update is badgeCount (setInterval 500 ms),
 *      which reads a cheap O(1) integer from warningStore.
 * ────────────────────────────────────────────────────────────────────────────
 *
 * Changes from previous version:
 *   • Added props: criticalThreshold, normalRange (min/max)
 *   • Added hooks.draw entry that draws:
 *       - Critical zone fill (rgba above threshold)
 *       - Dashed red threshold line
 *       - "CRITICAL: X,XXX psi" label (right-aligned)
 *       - "Normal: min–max psi" label (bottom-left)
 *     All drawing uses u.bbox and u.valToPos so it resizes correctly.
 *   • Everything else is identical to the previous version.
 */

import { useEffect, useRef, useState } from 'react';
import { TriangleAlert } from 'lucide-react';
import uPlot from 'uplot';
import 'uplot/dist/uPlot.min.css';
import type { TelemetryPacket } from '../services/signalr';
import { getWarningCount } from '../services/warningStore';
import { WarningPanel } from './WarningPanel';

interface NormalRange {
    min: number;
    max: number;
}

interface StreamingChartProps {
    sensorId: number;
    title: string;
    maxPoints?: number;
    accentColor?: string;
    /** Y value at which the dashed red critical-threshold line is drawn. */
    criticalThreshold: number;
    /** Min/max of the normal operating range shown as a label. */
    normalRange: NormalRange;
    /** Engineering unit string shown in labels (e.g. "psi", "g/s", "mm"). */
    unit?: string;
}

export function StreamingChart({
    sensorId,
    title,
    maxPoints = 200,
    accentColor = '#22d3ee', // cyan-400
    criticalThreshold,
    normalRange,
    unit = 'psi',
}: StreamingChartProps) {
    // ── Warning UI state ──────────────────────────────────────────────────────

    /** Fires only on user click — does NOT fire on packet arrival. */
    const [panelOpen, setPanelOpen] = useState(false);

    /**
     * Updated every 500 ms by setInterval — the ONLY allowed periodic state update.
     */
    const [badgeCount, setBadgeCount] = useState(0);

    useEffect(() => {
        const intervalId = setInterval(() => {
            setBadgeCount(getWarningCount(sensorId));
        }, 500);
        return () => clearInterval(intervalId);
    }, [sensorId]);

    // ── Chart logic ───────────────────────────────────────────────────────────

    const chartContainerRef = useRef<HTMLDivElement>(null);
    const uPlotRef = useRef<uPlot | null>(null);

    // Mutable buffers for X (time) and Y (value)
    const dataRef = useRef<[number[], number[]]>([[], []]);

    useEffect(() => {
        if (!chartContainerRef.current) return;

        // ── Threshold draw hook ───────────────────────────────────────────────
        //
        // Runs inside uPlot's render loop automatically on every redraw
        // (including after setData and browser resize).  No React state
        // is touched here — this is pure canvas imperative code.
        //
        // Coordinate system:
        //   u.bbox   = plot area in CSS pixels (excludes axes/padding)
        //   valToPos = converts a data-space Y value to a CSS-pixel Y coordinate
        //
        const drawThreshold = (u: uPlot) => {
            const ctx    = u.ctx;
            const { left, top, width, height } = u.bbox;

            // Convert the threshold value to a canvas Y coordinate.
            // valToPos returns undefined if the axis hasn't been scaled yet.
            const threshY = u.valToPos(criticalThreshold, 'y', true);
            if (threshY === undefined || threshY === null) return;

            // Clamp to the plot area — if the threshold is off-screen don't draw
            if (threshY < top || threshY > top + height) return;

            ctx.save();

            // ── 1. Critical zone fill (above the line to chart top) ───────────
            ctx.beginPath();
            ctx.fillStyle = 'rgba(239, 68, 68, 0.06)';
            ctx.fillRect(left, top, width, threshY - top);

            // ── 2. Dashed red threshold line ──────────────────────────────────
            ctx.beginPath();
            ctx.setLineDash([6, 3]);
            ctx.strokeStyle = '#ef4444';
            ctx.globalAlpha = 0.8;
            ctx.lineWidth   = 1.5;
            ctx.moveTo(left, threshY);
            ctx.lineTo(left + width, threshY);
            ctx.stroke();
            ctx.setLineDash([]);   // reset so subsequent draws aren't dashed
            ctx.globalAlpha = 1;

            // ── 3. "CRITICAL: X,XXX psi" label — right-aligned, 4px above line
            const critLabel = `CRITICAL: ${criticalThreshold.toLocaleString()} ${unit}`;
            ctx.font         = 'bold 10px monospace';
            ctx.fillStyle    = '#ef4444';
            ctx.globalAlpha  = 0.9;
            ctx.textAlign    = 'right';
            ctx.textBaseline = 'bottom';
            ctx.fillText(critLabel, left + width - 2, threshY - 4);

            // ── 4. "Normal: min–max psi" label — bottom-left of plot area ─────
            const normalLabel = `Normal: ${normalRange.min.toLocaleString()}–${normalRange.max.toLocaleString()} ${unit}`;
            ctx.font         = '9px monospace';
            ctx.fillStyle    = '#64748b'; // slate-500
            ctx.globalAlpha  = 0.85;
            ctx.textAlign    = 'left';
            ctx.textBaseline = 'bottom';
            ctx.fillText(normalLabel, left + 2, top + height - 4);

            ctx.restore();
        };

        const opts: uPlot.Options = {
            width: chartContainerRef.current.clientWidth || 300,
            height: 180,
            title: title,
            pxAlign: 0,
            cursor: { show: false },
            select: { show: false, left: 0, top: 0, width: 0, height: 0 },
            legend: { show: false },
            axes: [
                {
                    show: true,
                    stroke: '#64748b',
                    grid: { show: false },
                },
                {
                    show: true,
                    stroke: '#64748b',
                    grid: { stroke: '#1e293b' },
                },
            ],
            series: [
                {},
                {
                    show: true,
                    stroke: accentColor,
                    width: 2,
                    fill: accentColor + '20',
                    points: { show: false },
                },
            ],
            // hooks.draw fires after each full redraw (data update, resize, etc.)
            // Threshold lines are drawn here so they're always in sync with the
            // plot coordinate system — no manual resize listeners needed.
            hooks: {
                draw: [drawThreshold],
            },
        };

        uPlotRef.current = new uPlot(opts, [[], []], chartContainerRef.current);

        return () => {
            uPlotRef.current?.destroy();
            uPlotRef.current = null;
        };
    // criticalThreshold and normalRange are stable (from a constant) so listing
    // them here is safe and allows the chart to re-init if they ever change.
    }, [title, accentColor, criticalThreshold, normalRange, unit]);

    // This method is called via window event to avoid React renders
    const pushData = (packet: TelemetryPacket) => {
        if (!uPlotRef.current) return;

        const [x, y] = dataRef.current;

        x.push(packet.timestamp / 1000);
        y.push(packet.value);

        if (x.length > maxPoints) {
            x.shift();
            y.shift();
        }

        if (x.length > 1) {
            uPlotRef.current.setData([x, y]);
        }
    };

    // Listen to custom events dispatched by App.tsx
    useEffect(() => {
        const handleNewPacket = (e: CustomEvent<TelemetryPacket>) => {
            if (e.detail.sensorId === sensorId) {
                pushData(e.detail);
            }
        };

        window.addEventListener('telemetry-packet', handleNewPacket as EventListener);
        return () => {
            window.removeEventListener('telemetry-packet', handleNewPacket as EventListener);
        };
    }, [sensorId, maxPoints]);

    // ── Render ────────────────────────────────────────────────────────────────

    return (
        <>
            <div className="bg-slate-900/50 backdrop-blur-md border border-slate-800 p-4 rounded-xl shadow-xl flex flex-col h-[240px]">
                {/* ── Card header ─────────────────────────────────────────── */}
                <div className="flex justify-between items-center mb-2">
                    <h3 className="text-slate-300 font-mono text-xs uppercase tracking-wider">
                        {title}
                    </h3>

                    <div className="flex items-center gap-2">
                        {/* Sensor ID label */}
                        <span className="text-xs text-slate-500 font-mono">ID: {sensorId}</span>

                        {/* ── Warning button ─────────────────────────────── */}
                        <button
                            onClick={() => setPanelOpen(true)}
                            title="View Warnings"
                            aria-label={`View warnings for sensor ${sensorId}`}
                            className="relative flex items-center justify-center w-6 h-6 rounded transition-colors hover:bg-amber-400/10 focus:outline-none focus:ring-1 focus:ring-amber-400/50"
                        >
                            <TriangleAlert size={14} className="text-amber-400" />

                            {/* Badge — only visible when there are warnings */}
                            {badgeCount > 0 && (
                                <span
                                    className="absolute flex items-center justify-center font-mono font-bold leading-none"
                                    style={{
                                        top: '-5px',
                                        right: '-6px',
                                        minWidth: '15px',
                                        height: '15px',
                                        fontSize: '9px',
                                        padding: '0 2px',
                                        borderRadius: '8px',
                                        background: '#ef4444',
                                        color: '#fff',
                                        boxShadow: '0 0 0 1.5px #0f172a',
                                        pointerEvents: 'none',
                                    }}
                                >
                                    {badgeCount > 999 ? '999+' : badgeCount}
                                </span>
                            )}
                        </button>
                    </div>
                </div>

                {/* ── uPlot chart container ───────────────────────────────── */}
                <div ref={chartContainerRef} className="flex-1 overflow-hidden" />
            </div>

            {/* ── Warning panel (fixed-positioned drawer) ─────────────────── */}
            <WarningPanel
                sensorId={sensorId}
                sensorTitle={title}
                isOpen={panelOpen}
                onClose={() => setPanelOpen(false)}
            />
        </>
    );
}
