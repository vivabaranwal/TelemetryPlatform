/**
 * WarningPanel.tsx
 *
 * Right-side drawer that shows per-sensor anomaly history.
 *
 * Design constraints enforced:
 *  - Reads warnings on-demand from warningStore (pull, not push).
 *  - No useState that fires on every packet.
 *  - useEffect dependencies are filter states / isOpen — NOT packet counters.
 *  - Excel export (SheetJS) only runs on explicit user click.
 *
 * Props:
 *   sensorId    – numeric sensor ID
 *   sensorTitle – display name, e.g. "Hydraulic Pump A"
 *   isOpen      – controls visibility
 *   onClose     – called when user closes the panel
 */

import { useEffect, useMemo, useState } from 'react';
import { X, Download, AlertTriangle } from 'lucide-react';
import * as XLSX from 'xlsx';
import { getWarnings } from '../services/warningStore';
import type { AlertFrame } from '../services/warningStore';

// ── Types & constants ────────────────────────────────────────────────────────

type Duration = '1min' | '5min' | '15min' | 'all';

interface SeverityFilter {
    warning: boolean;
    critical: boolean;
}

const DURATION_OPTIONS: { value: Duration; label: string; ms: number }[] = [
    { value: '1min',  label: 'Last 1 minute',  ms: 60_000 },
    { value: '5min',  label: 'Last 5 minutes',  ms: 300_000 },
    { value: '15min', label: 'Last 15 minutes', ms: 900_000 },
    { value: 'all',   label: 'All time',         ms: Infinity },
];

/** Maximum rows rendered in the DOM to avoid layout overload. */
const MAX_DISPLAY_ROWS = 500;

// ── Helpers ──────────────────────────────────────────────────────────────────

/**
 * Returns engineering unit for a sensor, based on the same modulo rule
 * used by the SensorSimulator (ID % 3 == 0 → hydraulic psi, 1 → fuel g/s, 2 → mm).
 */
function getSensorUnit(sensorId: number): string {
    switch (sensorId % 3) {
        case 0: return 'psi';
        case 1: return 'g/s';
        default: return 'mm';
    }
}

/** Format a raw value with locale separators and engineering unit. */
function formatValue(value: number, sensorId: number): string {
    const unit = getSensorUnit(sensorId);
    return `${value.toLocaleString('en-US', { maximumFractionDigits: 2 })} ${unit}`;
}

/** Format a Unix-ms timestamp as HH:MM:SS.mmm */
function formatTimestamp(ts: number): string {
    const d = new Date(ts);
    const hh = String(d.getHours()).padStart(2, '0');
    const mm = String(d.getMinutes()).padStart(2, '0');
    const ss = String(d.getSeconds()).padStart(2, '0');
    const ms = String(d.getMilliseconds()).padStart(3, '0');
    return `${hh}:${mm}:${ss}.${ms}`;
}

/**
 * Derives a human-readable "Threshold Breached" string from the
 * AlertFrame.message field emitted by AnomalyDetectionService.
 *
 * Backend message examples:
 *   "Z-score 2.34 >= warning threshold 2.00"
 *   "Z-score 3.13 >= 3.00 (value=1143.58, mean=1207.78, σ=20.50)"
 *   "Value 3710.90 > absolute threshold 3500.00 AND Z-score 3.86 >= 3.00 (...)"
 *
 * NOTE: alert.message may be undefined/null for some backend severity levels.
 */
function getThresholdBreached(alert: AlertFrame): string {
    const msg = alert.message ?? '';
    if (!msg) {
        // Fallback when message is absent — derive from severity
        return alert.severity === 2 ? 'Abs > 3500 & Z ≥ 3.0' : 'Z-Score ≥ 2.0';
    }

    if (msg.includes('absolute threshold')) {
        const absMatch = msg.match(/absolute threshold ([\d.]+)/);
        const absVal = absMatch ? parseFloat(absMatch[1]).toLocaleString() : '3500';
        const zMatch  = msg.match(/Z-score [\d.]+ >= ([\d.]+)/);
        if (zMatch) {
            return `Abs > ${absVal} & Z ≥ ${zMatch[1]}`;
        }
        return `Abs > ${absVal}`;
    }

    if (msg.includes('warning threshold')) {
        const zMatch = msg.match(/warning threshold ([\d.]+)/);
        return `Z-Score ≥ ${zMatch ? zMatch[1] : '2.0'}`;
    }

    const zMatch = msg.match(/>= ([\d.]+)/);
    return `Z-Score ≥ ${zMatch ? zMatch[1] : '3.0'}`;
}

/** Slugify sensor title for use in filenames, e.g. "Hydraulic Pump A" → "HydraulicPumpA" */
function slugify(title: string): string {
    return title.replace(/\s+/g, '');
}

/** Current date-time as YYYY-MM-DD_HH-MM string for filenames. */
function fileDateStamp(): string {
    const d = new Date();
    const date = d.toISOString().slice(0, 10);
    const hhmm = `${String(d.getHours()).padStart(2, '0')}-${String(d.getMinutes()).padStart(2, '0')}`;
    return `${date}_${hhmm}`;
}

// ── Component ────────────────────────────────────────────────────────────────

interface WarningPanelProps {
    sensorId: number;
    sensorTitle: string;
    isOpen: boolean;
    onClose: () => void;
}

export function WarningPanel({ sensorId, sensorTitle, isOpen, onClose }: WarningPanelProps) {
    // Filter state — user-driven, not packet-driven
    const [duration, setDuration] = useState<Duration>('all');
    const [severityFilter, setSeverityFilter] = useState<SeverityFilter>({ warning: true, critical: true });

    // Snapshot of warnings when the panel is open / filter changes.
    // This is safe: it only updates when the user interacts with filters.
    const [snapshot, setSnapshot] = useState<AlertFrame[]>([]);

    // Take a fresh snapshot from the store whenever:
    //  - the panel opens/closes
    //  - the user changes a filter (duration or severity)
    useEffect(() => {
        if (!isOpen) return;
        setSnapshot(getWarnings(sensorId));
    }, [isOpen, sensorId, duration, severityFilter]);

    // Apply duration + severity filters purely in memory — no API calls
    const filteredRows = useMemo<AlertFrame[]>(() => {
        if (!isOpen) return [];

        const cutoffMs = DURATION_OPTIONS.find(d => d.value === duration)?.ms ?? Infinity;
        const now = Date.now();

        return snapshot
            .filter(w => (now - w.timestamp) <= cutoffMs)
            .filter(w => {
                if (w.severity === 2) return severityFilter.critical;
                if (w.severity === 1) return severityFilter.warning;
                return false;
            })
            .slice(0, MAX_DISPLAY_ROWS);
    }, [snapshot, duration, severityFilter, isOpen]);

    // ── Excel export ──────────────────────────────────────────────────────────

    function handleExport() {
        const unit = getSensorUnit(sensorId);
        const durationLabel = DURATION_OPTIONS.find(d => d.value === duration)?.label ?? 'All time';

        // ── Sheet 1: Warnings table ───────────────────────────────────────────
        const warningRows = filteredRows.map(w => ({
            'Timestamp':          formatTimestamp(w.timestamp),
            'Value':              formatValue(w.value, sensorId),
            'Z-Score':            w.zScore.toFixed(2),
            'Severity':           w.severity === 2 ? 'CRITICAL' : 'WARNING',
            'Threshold Breached': getThresholdBreached(w),
        }));

        const wsWarnings = XLSX.utils.json_to_sheet(
            warningRows.length > 0 ? warningRows : [{ 'Timestamp': 'No data', 'Value': '', 'Z-Score': '', 'Severity': '', 'Threshold Breached': '' }]
        );

        // ── Sheet 2: Summary ──────────────────────────────────────────────────
        const criticalCount = filteredRows.filter(w => w.severity === 2).length;
        const warningCount  = filteredRows.filter(w => w.severity === 1).length;
        const values        = filteredRows.map(w => w.value);
        const avgZScore     = filteredRows.length > 0
            ? filteredRows.reduce((sum, w) => sum + w.zScore, 0) / filteredRows.length
            : 0;

        const minVal = values.length > 0 ? `${Math.min(...values).toLocaleString('en-US', { maximumFractionDigits: 2 })} ${unit}` : '-';
        const maxVal = values.length > 0 ? `${Math.max(...values).toLocaleString('en-US', { maximumFractionDigits: 2 })} ${unit}` : '-';

        const summaryRows = [
            { 'Field': 'Sensor Name',    'Value': sensorTitle },
            { 'Field': 'Sensor ID',      'Value': sensorId },
            { 'Field': 'Export Time',    'Value': new Date().toLocaleString('en-US', { hour12: false }) },
            { 'Field': 'Duration Filter','Value': durationLabel },
            { 'Field': 'Total Warnings', 'Value': filteredRows.length },
            { 'Field': 'Critical Count', 'Value': criticalCount },
            { 'Field': 'Warning Count',  'Value': warningCount },
            { 'Field': 'Min Value',      'Value': minVal },
            { 'Field': 'Max Value',      'Value': maxVal },
            { 'Field': 'Avg Z-Score',    'Value': avgZScore.toFixed(2) },
        ];
        const wsSummary = XLSX.utils.json_to_sheet(summaryRows);

        // ── Write workbook ────────────────────────────────────────────────────
        const wb = XLSX.utils.book_new();
        XLSX.utils.book_append_sheet(wb, wsWarnings, 'Warnings');
        XLSX.utils.book_append_sheet(wb, wsSummary,  'Summary');

        const filename = `${slugify(sensorTitle)}_warnings_${fileDateStamp()}.xlsx`;
        XLSX.writeFile(wb, filename);
    }

    // ── Severity toggle helper ────────────────────────────────────────────────
    function toggleSeverity(key: keyof SeverityFilter) {
        setSeverityFilter(prev => ({ ...prev, [key]: !prev[key] }));
    }

    // ── Keyboard close ────────────────────────────────────────────────────────
    useEffect(() => {
        if (!isOpen) return;
        const handler = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
        window.addEventListener('keydown', handler);
        return () => window.removeEventListener('keydown', handler);
    }, [isOpen, onClose]);

    if (!isOpen) return null;

    // ── Render ────────────────────────────────────────────────────────────────
    return (
        /* Backdrop */
        <div
            className="fixed inset-0 z-50 flex justify-end"
            style={{ backgroundColor: 'rgba(0,0,0,0.6)', backdropFilter: 'blur(2px)' }}
            onClick={(e) => { if (e.target === e.currentTarget) onClose(); }}
        >
            {/* Drawer panel */}
            <div
                className="relative h-full flex flex-col shadow-2xl"
                style={{
                    width: 'min(680px, 95vw)',
                    background: 'linear-gradient(180deg, #0c1428 0%, #080e1c 100%)',
                    borderLeft: '1px solid #1e3a5f',
                }}
                onClick={(e) => e.stopPropagation()}
            >
                {/* ── HEADER ─────────────────────────────────────────────────── */}
                <div
                    className="flex items-center justify-between px-6 py-4 shrink-0"
                    style={{ borderBottom: '1px solid #1e3a5f', background: 'rgba(0,229,255,0.03)' }}
                >
                    <div className="flex items-center gap-3">
                        <AlertTriangle size={18} className="text-amber-400" />
                        <div>
                            <h2 className="font-mono text-sm font-bold text-slate-100 uppercase tracking-widest">
                                {sensorTitle}
                            </h2>
                            <p className="font-mono text-xs text-slate-500 mt-0.5">
                                Sensor ID: {sensorId} &nbsp;·&nbsp;
                                <span className="text-amber-400">{filteredRows.length} records shown</span>
                            </p>
                        </div>
                    </div>

                    <div className="flex items-center gap-2">
                        {/* Export button */}
                        <button
                            onClick={handleExport}
                            disabled={filteredRows.length === 0}
                            title="Export visible rows as Excel"
                            className="flex items-center gap-1.5 px-3 py-1.5 rounded text-xs font-mono font-semibold transition-all"
                            style={{
                                background: filteredRows.length > 0 ? 'rgba(0,229,255,0.1)' : 'rgba(255,255,255,0.03)',
                                border: `1px solid ${filteredRows.length > 0 ? '#00e5ff' : '#334155'}`,
                                color: filteredRows.length > 0 ? '#00e5ff' : '#475569',
                                cursor: filteredRows.length > 0 ? 'pointer' : 'not-allowed',
                            }}
                        >
                            <Download size={13} />
                            Export Excel
                        </button>

                        {/* Close button */}
                        <button
                            onClick={onClose}
                            title="Close panel"
                            className="p-1.5 rounded transition-colors hover:bg-slate-800"
                            style={{ color: '#64748b' }}
                        >
                            <X size={18} />
                        </button>
                    </div>
                </div>

                {/* ── FILTER BAR ─────────────────────────────────────────────── */}
                <div
                    className="flex flex-wrap items-center gap-4 px-6 py-3 shrink-0"
                    style={{ borderBottom: '1px solid #1a2d4a' }}
                >
                    {/* Duration dropdown */}
                    <div className="flex items-center gap-2">
                        <label className="font-mono text-xs text-slate-500 uppercase tracking-wider">
                            Duration
                        </label>
                        <select
                            value={duration}
                            onChange={(e) => setDuration(e.target.value as Duration)}
                            className="font-mono text-xs rounded px-2 py-1 outline-none focus:ring-1 focus:ring-cyan-500"
                            style={{
                                background: '#0f1e35',
                                border: '1px solid #1e3a5f',
                                color: '#94a3b8',
                            }}
                        >
                            {DURATION_OPTIONS.map(opt => (
                                <option key={opt.value} value={opt.value}>{opt.label}</option>
                            ))}
                        </select>
                    </div>

                    {/* Severity toggles */}
                    <div className="flex items-center gap-2">
                        <label className="font-mono text-xs text-slate-500 uppercase tracking-wider">
                            Severity
                        </label>

                        {/* WARNING toggle */}
                        <button
                            onClick={() => toggleSeverity('warning')}
                            className="flex items-center gap-1.5 px-2.5 py-1 rounded text-xs font-mono font-semibold transition-all"
                            style={{
                                background: severityFilter.warning ? 'rgba(251,191,36,0.15)' : 'rgba(255,255,255,0.03)',
                                border: `1px solid ${severityFilter.warning ? '#f59e0b' : '#334155'}`,
                                color: severityFilter.warning ? '#fbbf24' : '#475569',
                            }}
                        >
                            <span
                                style={{
                                    display: 'inline-block',
                                    width: 8,
                                    height: 8,
                                    borderRadius: '50%',
                                    background: severityFilter.warning ? '#f59e0b' : '#475569',
                                }}
                            />
                            WARNING
                        </button>

                        {/* CRITICAL toggle */}
                        <button
                            onClick={() => toggleSeverity('critical')}
                            className="flex items-center gap-1.5 px-2.5 py-1 rounded text-xs font-mono font-semibold transition-all"
                            style={{
                                background: severityFilter.critical ? 'rgba(239,68,68,0.15)' : 'rgba(255,255,255,0.03)',
                                border: `1px solid ${severityFilter.critical ? '#ef4444' : '#334155'}`,
                                color: severityFilter.critical ? '#f87171' : '#475569',
                            }}
                        >
                            <span
                                style={{
                                    display: 'inline-block',
                                    width: 8,
                                    height: 8,
                                    borderRadius: '50%',
                                    background: severityFilter.critical ? '#ef4444' : '#475569',
                                }}
                            />
                            CRITICAL
                        </button>
                    </div>

                    {/* Live hint */}
                    <span className="ml-auto font-mono text-[10px] text-slate-600 italic">
                        Data captured since dashboard load · refresh filters to update
                    </span>
                </div>

                {/* ── TABLE ──────────────────────────────────────────────────── */}
                <div className="flex-1 overflow-y-auto" style={{ minHeight: 0 }}>
                    {filteredRows.length === 0 ? (
                        /* Empty state */
                        <div className="flex flex-col items-center justify-center h-full gap-3 text-slate-600">
                            <AlertTriangle size={32} className="opacity-30" />
                            <p className="font-mono text-sm italic">
                                No warnings recorded for this sensor yet.
                            </p>
                        </div>
                    ) : (
                        <table className="w-full text-left" style={{ borderCollapse: 'separate', borderSpacing: 0 }}>
                            <thead>
                                <tr style={{ background: '#0a1628', position: 'sticky', top: 0, zIndex: 10 }}>
                                    {['Timestamp', 'Value', 'Z-Score', 'Severity', 'Threshold Breached'].map(col => (
                                        <th
                                            key={col}
                                            className="font-mono text-[10px] uppercase tracking-widest text-slate-500 px-4 py-2"
                                            style={{ borderBottom: '1px solid #1e3a5f' }}
                                        >
                                            {col}
                                        </th>
                                    ))}
                                </tr>
                            </thead>
                            <tbody>
                                {filteredRows.map((w, idx) => {
                                    const isCritical = w.severity === 2;
                                    const rowBg = idx % 2 === 0 ? 'rgba(15,30,55,0.4)' : 'transparent';

                                    return (
                                        <tr
                                            key={`${w.timestamp}-${idx}`}
                                            style={{ background: rowBg }}
                                            className="transition-colors hover:bg-slate-800/40"
                                        >
                                            {/* Timestamp */}
                                            <td className="px-4 py-2 font-mono text-xs text-slate-400 whitespace-nowrap">
                                                {formatTimestamp(w.timestamp)}
                                            </td>

                                            {/* Value */}
                                            <td
                                                className="px-4 py-2 font-mono text-xs font-semibold whitespace-nowrap"
                                                style={{ color: isCritical ? '#f87171' : '#fbbf24' }}
                                            >
                                                {formatValue(w.value, sensorId)}
                                            </td>

                                            {/* Z-Score */}
                                            <td className="px-4 py-2 font-mono text-xs text-slate-300 whitespace-nowrap">
                                                {w.zScore.toFixed(2)}
                                            </td>

                                            {/* Severity badge */}
                                            <td className="px-4 py-2 whitespace-nowrap">
                                                <span
                                                    className="inline-block font-mono text-[10px] font-bold px-2 py-0.5 rounded-sm uppercase tracking-wider"
                                                    style={{
                                                        background: isCritical ? 'rgba(239,68,68,0.15)' : 'rgba(251,191,36,0.15)',
                                                        border:     `1px solid ${isCritical ? '#ef4444' : '#f59e0b'}`,
                                                        color:      isCritical ? '#f87171' : '#fbbf24',
                                                    }}
                                                >
                                                    {isCritical ? 'CRITICAL' : 'WARNING'}
                                                </span>
                                            </td>

                                            {/* Threshold Breached */}
                                            <td className="px-4 py-2 font-mono text-[11px] text-slate-500 whitespace-nowrap">
                                                {getThresholdBreached(w)}
                                            </td>
                                        </tr>
                                    );
                                })}
                            </tbody>
                        </table>
                    )}
                </div>

                {/* ── FOOTER ─────────────────────────────────────────────────── */}
                {filteredRows.length >= MAX_DISPLAY_ROWS && (
                    <div
                        className="px-6 py-2 text-center font-mono text-[10px] text-slate-600 italic shrink-0"
                        style={{ borderTop: '1px solid #1a2d4a' }}
                    >
                        Showing first {MAX_DISPLAY_ROWS} matching records.
                        Store holds up to 1,000 entries per sensor.
                    </div>
                )}
            </div>
        </div>
    );
}
