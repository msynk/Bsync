using Bsync.Samples.Tasks.Server;

// ConnectionStrings:Tasks names the application's SQL Server database, for example
// "Server=(localdb)\MSSQLLocalDB;Database=BsyncTasks;Integrated Security=true;TrustServerCertificate=true".
var app = await TasksServer.BuildAsync(args);
app.Run();
