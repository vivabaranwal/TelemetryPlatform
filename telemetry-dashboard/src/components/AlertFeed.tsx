import { useEffect, useState } from 'react';
import { AlertTriangle, ShieldAlert } from 'lucide-react';
import type { AlertFrame } from '../services/signalr';
import { signalRService } from '../services/signalr';

export function AlertFeed() {
    const [alerts, setAlerts] = useState<AlertFrame[]>([]);

    useEffect(() => {
        const unsubscribe = signalRService.onAlert((alert) => {
            setAlerts(prev => {
                // Keep only the latest 50 alerts
                const newAlerts = [alert, ...prev];
                if (newAlerts.length > 50) return newAlerts.slice(0, 50);
                return newAlerts;
            });
        });

        return () => unsubscribe();
    }, []);

    const formatTime = (ts: number) => {
        return new Date(ts).toLocaleTimeString('en-US', { hour12: false, fractionalSecondDigits: 3 });
    };

    return (
        <div className="bg-slate-900/40 backdrop-blur-md border-l border-slate-800 h-full flex flex-col">
            <div className="p-4 border-b border-slate-800">
                <h2 className="text-sm font-mono text-slate-300 uppercase tracking-widest flex items-center gap-2">
                    <ShieldAlert size={16} className="text-cyan-500" />
                    Anomaly Feed
                </h2>
            </div>
            
            <div className="flex-1 overflow-y-auto p-4 space-y-3">
                {alerts.length === 0 ? (
                    <div className="text-slate-500 text-sm font-mono italic text-center mt-10">
                        System nominal. No anomalies detected.
                    </div>
                ) : (
                    alerts.map((alert, idx) => {
                        const isCritical = alert.severity === 2;
                        return (
                            <div 
                                key={`${alert.timestamp}-${idx}`} 
                                className={`p-3 rounded-lg border ${
                                    isCritical 
                                        ? 'bg-red-950/30 border-red-900/50 text-red-200' 
                                        : 'bg-amber-950/30 border-amber-900/50 text-amber-200'
                                }`}
                            >
                                <div className="flex items-start gap-3">
                                    <AlertTriangle size={16} className={`mt-0.5 shrink-0 ${isCritical ? 'text-red-500' : 'text-amber-500'}`} />
                                    <div className="flex-1 min-w-0">
                                        <div className="flex justify-between items-baseline mb-1">
                                            <span className="font-mono text-xs font-bold shrink-0">
                                                {formatTime(alert.timestamp)}
                                            </span>
                                            <span className={`text-[10px] uppercase tracking-wider px-1.5 py-0.5 rounded-sm ${
                                                isCritical ? 'bg-red-500/20 text-red-400' : 'bg-amber-500/20 text-amber-400'
                                            }`}>
                                                Z={alert.zScore.toFixed(1)}
                                            </span>
                                        </div>
                                        <p className="text-xs opacity-90 truncate" title={alert.message}>
                                            {alert.message}
                                        </p>
                                    </div>
                                </div>
                            </div>
                        );
                    })
                )}
            </div>
        </div>
    );
}
