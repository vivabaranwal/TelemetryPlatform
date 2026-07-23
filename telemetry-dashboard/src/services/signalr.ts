import * as signalR from "@microsoft/signalr";

export interface TelemetryPacket {
    timestamp: number;
    sensorId: number;
    value: number;
    flags: number;
}

export interface AlertFrame {
    timestamp: number;
    sensorId: number;
    value: number;
    zScore: number;
    severity: number; // 0=Normal, 1=Warning, 2=Critical
    message: string;
}

class TelemetrySignalRService {
    private connection: signalR.HubConnection | null = null;
    private packetListeners: ((packet: TelemetryPacket) => void)[] = [];
    private alertListeners: ((alert: AlertFrame) => void)[] = [];

    public async connect(url: string = "http://localhost:5000/hubs/telemetry") {
        if (this.connection) return;

        this.connection = new signalR.HubConnectionBuilder()
            .withUrl(url)
            .withAutomaticReconnect([0, 2000, 5000, 10000]) // Custom reconnect intervals
            .configureLogging(signalR.LogLevel.Information)
            .build();

        this.connection.on("ReceiveTelemetry", (packet: TelemetryPacket) => {
            console.log("Raw ReceiveTelemetry invoked, sensorId:", packet.sensorId, "listeners count:", this.packetListeners.length);
            for (const listener of this.packetListeners) {
                listener(packet);
            }
        });

        this.connection.on("ReceiveAlert", (alert: AlertFrame) => {
            for (const listener of this.alertListeners) {
                listener(alert);
            }
        });

        try {
            await this.connection.start();
            console.log("SignalR Connected to Telemetry Hub");
        } catch (err) {
            console.error("SignalR Connection Error: ", err);
        }
    }

    public onTelemetry(listener: (packet: TelemetryPacket) => void) {
        this.packetListeners.push(listener);
        return () => {
            this.packetListeners = this.packetListeners.filter(l => l !== listener);
        };
    }

    public onAlert(listener: (alert: AlertFrame) => void) {
        this.alertListeners.push(listener);
        return () => {
            this.alertListeners = this.alertListeners.filter(l => l !== listener);
        };
    }
    
    public getConnectionState() {
        return this.connection?.state ?? signalR.HubConnectionState.Disconnected;
    }
}

export const signalRService = new TelemetrySignalRService();
