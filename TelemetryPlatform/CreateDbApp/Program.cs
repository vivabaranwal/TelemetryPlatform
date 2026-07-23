using Npgsql;
using System;

var connStr = "Host=localhost;Username=postgres;Password=Viva@2006";
using var conn = new NpgsqlConnection(connStr);
try {
    conn.Open();
    using var cmd = new NpgsqlCommand("CREATE DATABASE telemetry;", conn);
    cmd.ExecuteNonQuery();
    Console.WriteLine("DB created");
} catch(Exception e) {
    Console.WriteLine(e.Message);
}
