const signalR = require("@microsoft/signalr");

const connection = new signalR.HubConnectionBuilder()
    .withUrl("http://localhost:5000/hubs/telemetry")
    .build();

connection.start().then(() => {
    console.log("Connected to SignalR!");
}).catch(console.error);

setTimeout(() => {
    console.log("Waiting to see if it crashes...");
}, 8000);
