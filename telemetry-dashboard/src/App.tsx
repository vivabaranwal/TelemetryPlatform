import { useEffect, useState, useRef } from 'react';
import { signalRService } from './services/signalr';
import { StreamingChart } from './components/StreamingChart';
import { AlertFeed } from './components/AlertFeed';
import { SimulationButton } from './components/SimulationButton';
import { Activity, Radio } from 'lucide-react';

// Sensor configuration — includes per-sensor threshold values for chart overlays.
// criticalThreshold matches the AnomalyDetectionService absolute threshold in the backend.
const SENSORS = [
  {
    id: 0, title: 'Hydraulic Pump A',
    criticalThreshold: 3500, normalRange: { min: 1500, max: 2500 }, unit: 'psi',
  },
  {
    id: 1, title: 'Fuel Regulator 1',
    criticalThreshold: 1800, normalRange: { min: 1200, max: 1800 }, unit: 'g/s',
  },
  {
    id: 2, title: 'Landing Gear Actuator',
    criticalThreshold: 3200, normalRange: { min: 1400, max: 2200 }, unit: 'mm',
  },
  {
    id: 3, title: 'Hydraulic Pump B',
    criticalThreshold: 3500, normalRange: { min: 1500, max: 2500 }, unit: 'psi',
  },
  {
    id: 4, title: 'Fuel Regulator 2',
    criticalThreshold: 1800, normalRange: { min: 1200, max: 1800 }, unit: 'g/s',
  },
  {
    id: 5, title: 'Aileron Actuator L',
    criticalThreshold: 3000, normalRange: { min: 1400, max: 2200 }, unit: 'mm',
  },
];

function App() {
  const [connected, setConnected] = useState(false);
  const [msgCount, setMsgCount] = useState(0);
  const msgCountRef = useRef(0);

  useEffect(() => {
    // 1. Connect to backend
    signalRService.connect().then(() => {
      setConnected(true);
    });

    // Sync ref to state every 100ms to avoid React render queue starvation
    const intervalId = setInterval(() => {
      setMsgCount(msgCountRef.current);
    }, 100);

    // 2. Global listener for raw telemetry packets
    const unsubscribe = signalRService.onTelemetry((packet) => {
      // TEMPORARY LOGGING TO DEBUG REFRESH ISSUE
      if (msgCountRef.current < 5) {
        console.log('Packet received in App.tsx:', packet);
      }
      
      msgCountRef.current++;
      
      // We dispatch a DOM event so child StreamingCharts can receive it
      // bypassing React's render cycle completely.
      const event = new CustomEvent('telemetry-packet', { detail: packet });
      window.dispatchEvent(event);
    });

    return () => {
      unsubscribe();
      clearInterval(intervalId);
    };
  }, []);

  return (
    <div className="flex h-screen overflow-hidden bg-slate-950">
      {/* Copyright footer — fixed to viewport bottom, never shifts layout */}
      <footer className="fixed bottom-0 left-0 right-0 z-40 border-t border-slate-800 bg-slate-950/80 backdrop-blur-sm py-1 text-center">
        <span className="text-xs text-slate-500 font-mono">
          Made by Viva Baranwal for HAL &copy; 2026
        </span>
      </footer>
      {/* Main Content Area */}
      <div className="flex-1 flex flex-col min-w-0">
        
        {/* Top HUD */}
        <header className="h-14 border-b border-slate-800 bg-slate-900/50 backdrop-blur-md flex items-center justify-between px-6 shrink-0">
          <div className="flex items-center gap-3">
            <Activity className="text-cyan-500" size={20} />
            <h1 className="font-mono text-sm uppercase tracking-widest text-slate-200">
              HAL Aerospace Telemetry
            </h1>
          </div>
          
          <div className="flex items-center gap-6">
            <div className="flex items-center gap-2">
              <span className="text-xs font-mono text-slate-500 uppercase">Packets Rx</span>
              <span className="font-mono text-sm text-cyan-400">{msgCount.toLocaleString()}</span>
            </div>

            {/* ── Simulation control ──────────────────────────────────── */}
            <SimulationButton />

            <div className="flex items-center gap-2">
              <span className="text-xs font-mono text-slate-500 uppercase">Status</span>
              <div className={`flex items-center gap-1.5 px-2 py-1 rounded-sm text-xs font-mono ${
                connected ? 'bg-cyan-950/50 text-cyan-400' : 'bg-slate-800 text-slate-400'
              }`}>
                <Radio size={14} className={connected ? 'animate-pulse' : ''} />
                {connected ? 'LIVE' : 'CONNECTING...'}
              </div>
            </div>
          </div>
        </header>

        {/* Charts Grid */}
        <main className="flex-1 p-6 overflow-y-auto">
          <div className="grid grid-cols-1 lg:grid-cols-2 xl:grid-cols-3 gap-6">
            {SENSORS.map(sensor => (
              <StreamingChart
                key={sensor.id}
                sensorId={sensor.id}
                title={sensor.title}
                maxPoints={200}
                accentColor="#22d3ee"
                criticalThreshold={sensor.criticalThreshold}
                normalRange={sensor.normalRange}
                unit={sensor.unit}
              />
            ))}
          </div>
        </main>
      </div>

      {/* Right Sidebar - Alert Feed */}
      <aside className="w-80 shrink-0">
        <AlertFeed />
      </aside>
    </div>
  );
}

export default App;
