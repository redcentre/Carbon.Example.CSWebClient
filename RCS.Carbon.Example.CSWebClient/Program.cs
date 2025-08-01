using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace RCS.Carbon.Example.CSWebClient;

/// <summary>
/// An example .NET client for the example Carbon web service that processes plain JSON
/// requests and responses. It does NOT use the RCS.Carbon.Examples.WebService.Common
/// NuGet package which provides strong class binding to the requests and response
/// for .NET clients. For more information see the GitHub repository readme.
/// </summary>
internal class Program
{
	static HttpResponseMessage? rm;
	static JsonDocument? doc;
	static HttpClient? client;
	static IConfiguration? config;
	const string SessionHeaderKey = "x-session-id";
	static string? sessionId;
	static string? id;
	static string? name;
	static string[]? roles;
	static readonly List<Cust> customers = [];
	static Job? selectedJob;
	static string[]? vartreeNames;

	// The following parameters come from the settings file first, then the
	// selected VS launch profile can override some values via the command
	// line for testing of different service Uris.

	static string? baseUri;
	static string? loginUserName;
	static string? loginPassword;
	static string? topVariable;
	static string? sideVariable;
	static string? filter;
	static string? weight;
	static string? outputFormat;

	static async Task Main(string[] args)
	{
		config = new ConfigurationBuilder()
			.AddJsonFile("appsettings.json")
			.AddCommandLine(args)
			.Build();
		baseUri = config["BaseUri"]!;
		loginUserName = config["UserName"]!;
		loginPassword = config["Password"]!;
		topVariable = config["Top"]!;
		sideVariable = config["Side"]!;
		filter = config["Filter"];
		weight = config["Weight"];
		outputFormat = config["Format"]!;
		client = new HttpClient
		{
			BaseAddress = new Uri(baseUri)
		};
		await SanityCheckServiceInfo();
		await StartNameSession();
		AskForJobToOpen();
		if (selectedJob != null)
		{
			await OpenJob();
			await ListJobVartrees();
			if (vartreeNames!.Length == 0)
			{
				Warn($"Job Id {selectedJob.Id} Name {selectedJob.Name} does not contain any variable trees");
			}
			else
			{
				await SetActiveVartree(vartreeNames[0]);
				await GetVartreeAsNodes();
			}
			await GenTab();
			await GenJson();
			await CloseJob();
		}
		await EndSession();
		client.Dispose();
		Pause();
	}

	/// <summary>
	/// Attempt to retrieve metadata information about the web service to sanity
	/// check it's reponding before continuing. Strange external errors can     
	/// occur making the service call, so we catch anything and report it and
	/// quit early.
	/// </summary>
	static async Task SanityCheckServiceInfo()
	{
		Sep("Service Info");
		Info($"Attempt to contact service at {baseUri}");
		try
		{
			rm = await client!.GetAsync("service/info");
			doc = await CheckResponse(rm);
			string? version = doc.RootElement.GetProperty("version").GetString();
			string? hostMachine = doc.RootElement.GetProperty("hostMachine").GetString();
			Debug.Assert(version != null && hostMachine != null);
			Info($"Contacted Carbon web service version {version} on machine {hostMachine}");
		}
		catch (Exception ex)
		{
			Fatal(ex.Message);
		}
	}

	/// <summary>
	/// Authenticate to the service using account name and password credentials.
	/// An OK response includes a 'sessionId' string which must be
	/// added to the client headers to allow subsequent privileged calls to proceed.The response
	/// also includes important account information such as a 'tree' of customers and child jobs
	/// that the account may access.                                                             
	/// </summary>
	static async Task StartNameSession()
	{
		Sep("Authenticate by account Name");
		var authRequest = new { name = loginUserName, password = loginPassword, skipCache = true, appId = "UnitTests" };
		rm = await client!.PostAsJsonAsync("session/start/authenticate/name", authRequest);
		if (rm.StatusCode == System.Net.HttpStatusCode.BadRequest)
		{
			string body = await rm.Content.ReadAsStringAsync();
			var errdoc = JsonDocument.Parse(body);
			int code = errdoc.RootElement.GetProperty("code").GetInt32();
			string message = errdoc.RootElement.GetProperty("message").GetString()!;
			string? detail = errdoc.RootElement.GetProperty("details").GetString();
			if (code == 302)
			{
				Warn($"Code {code} - {message}");
				Warn($"{detail}");
				Info($"Do you want to close other sessions and continue? (y/n)");
				string? r = Console.ReadLine();
				if (r != "y") Fatal("You declined to close other sessions - Abort");
				string[] ids = errdoc.RootElement.GetProperty("data").EnumerateArray().Select(x => x.GetString()!).ToArray();
				string join = string.Join(",", ids);
				rm = await client!.DeleteAsync($"session/force/{join}");
				var doc = await CheckResponse(rm);
				int count = doc.RootElement.GetInt32();
				Info($"Force closed {count} sessions");
			}
		}
		doc = await CheckResponse(rm);
		sessionId = doc.RootElement.GetProperty("sessionId").GetString();
		id = doc.RootElement.GetProperty("id").GetString();
		name = doc.RootElement.GetProperty("name").GetString();
		roles = doc.RootElement.GetProperty("roles").EnumerateArray().Select(x => x.GetString()!).ToArray();
		Info($"Session Id {sessionId} for account Id {id} Name {name}");
		Info($"Roles -> [{(string.Join(",", roles))}]");
		client!.DefaultRequestHeaders.Add(SessionHeaderKey, sessionId);
		// Walk down the customers and jobs to collect their details.
		var custsProp = doc.RootElement.GetProperty("sessionCusts");
		foreach (var custProp in custsProp.EnumerateArray())
		{
			string custId = custProp.GetProperty("id").GetString()!;
			string custName = custProp.GetProperty("name").GetString()!;
			string? custDisplayName = custProp.GetProperty("displayName").GetString();
			var cust = new Cust(custId, custName, custDisplayName);
			customers.Add(cust);
			Info($"CUST |  {custId} {custDisplayName ?? custName}");
			var jobsProp = custProp.GetProperty("sessionJobs");
			foreach (var jobProp in jobsProp.EnumerateArray())
			{
				string jobId = jobProp.GetProperty("id").GetString()!;
				string jobName = jobProp.GetProperty("name").GetString()!;
				string jobDisplayName = jobProp.GetProperty("displayName").GetString()!;
				var job = new Job(jobId, jobName, jobDisplayName, cust);
				cust.Jobs.Add(job);
				Info($"JOB  |  |  {jobId} {jobDisplayName ?? jobName}");
			}
		}
		var jobcount = customers.Sum(c => c.Jobs.Count);
		if (jobcount == 0) Fatal($"No jobs are assigned to account Name {name}");
	}

	/// <summary>
	/// Prompt the user to enter the sequence number of the job to open from a
	/// flattened list of Customer-Job pairs.
	/// </summary>
	static void AskForJobToOpen()
	{
		Sep("Select job");
		var flatquery = from Cust in customers
						from Job in Cust.Jobs
						select new { Cust, Job };
		var flattups = flatquery.ToArray();
		int? jobChoiceIx = null;
		Console.ForegroundColor = ConsoleColor.Cyan;
		foreach (var x in flattups.Select((t, i) => new { t.Cust, t.Job, Seq = i + 1 }))
		{
			Console.WriteLine($"{x.Seq,2} - {x.Cust.DisplayName ?? x.Cust.Name} - {x.Job.DisplayName ?? x.Job.Name}");
		}
		Console.WriteLine(" X - Exit");
		do
		{
			Console.Write("Enter job number to open: ");
			string? r = Console.ReadLine();
			if (r == "x" || r == "X")
			{
				jobChoiceIx = -1;
			}
			else
			{
				if (int.TryParse(r, out int parsei))
				{
					int ix = parsei - 1;
					if (ix >= 0 && ix < flattups.Length)
					{
						jobChoiceIx = ix;
						selectedJob = flattups[jobChoiceIx.Value].Job;
					}
				}
			}
		}
		while (jobChoiceIx == null);
	}

	/// <summary>
	/// Open the job previously selected from the list of those available to the account.
	/// Save the reponse JSON document because it contains information information about
	/// the job that can be used later.
	/// </summary>
	static async Task OpenJob()
	{
		Sep("Open job");
		var jobOpenRequest = new
		{
			customerName = selectedJob!.ParentCust.Name,
			jobName = selectedJob.Name,
			getDisplayProps = true,
			getVartreeNames = true,
			getAxisTreeNames = true,
			tocType = 0,
			getDrills = true
		};
		rm = await client!.PostAsJsonAsync("job/open", jobOpenRequest);
		await CheckResponse(rm);
	}

	/// <summary>
	/// List the vartree (Variable Tree) names available in the job.
	/// </summary>
	static async Task ListJobVartrees()
	{
		Sep("List Variable Tree Names");
		rm = await client!.GetAsync("job/vartree/list");
		doc = await CheckResponse(rm);
		vartreeNames = doc.RootElement.EnumerateArray().Select(x => x.GetString()).ToArray()!;
	}

	/// <summary>
	/// Set one of the available vartree names as the active one.
	/// </summary>
	static async Task SetActiveVartree(string vartreeName)
	{
		Sep($"Set active vartree");
		rm = await client!.GetAsync($"job/vartree/{vartreeName}");
		doc = await CheckResponse(rm);
		bool success = doc.RootElement.GetBoolean();
		Info($"Set vartree '{vartreeName}' as active -> {success}");
		if (!success) Fatal($"Set vartree '{vartreeName}' did not return success");
	}

	/// <summary>
	/// Get the currently active vartree as a hierarchy of GenNode objects. They could be
	/// used to make a 'variable picker', but for now they're just dumped.
	/// </summary>
	static async Task GetVartreeAsNodes()
	{
		Sep("Get vartree as nodes");
		rm = await client!.GetAsync("job/vartree/nodes");
		await CheckResponse(rm);
	}

	const string? caseFilter = null;
	const string? topInsert = null;
	const string? sideInsert = null;
	const string? level = null;

	/// <summary>
	/// NOTE: REPORT GENERATION IS CURRENTLY SUBJECT TO EXPERIMENTS TO DETERMINE HOW TO MAKE THE ENDPOINT
	/// EASIER TO USE BY NON-.NET CLIENTS. THE XDisplayProperties CLASS IS VERY LARGE AND REQUIRES ~600
	/// LINES OF JSON TO SERIALIZE, WHICH IS INCONVENIENT FOR CLIENTS THAT NEED TO CONSTRUCT THE REQUEST
	/// BODY WITH SLIGHTLY DIFFERENT VALUES IN DIFFERENT CALLS. THIS IS BEING INVESTIGATED.
	/// </summary>
	static async Task GenTab()
	{
		Sep("Generate Cross-tabulation Report");
		var genTabRequest = new
		{
			name = "Report-1",
			top = topVariable,
			side = sideVariable,
			filter,
			weight,
			sProps = new
			{
				caseFilter,
				topInsert,
				sideInsert,
				level,
				initAsMissing = true,
				excludeNE = true,
				padHierarchics = true,
				arithOverStats = true
			},
			dProps = new
			{
				// TODO ????????????????????????????
			}
		};
		rm = await client!.PostAsJsonAsync($"report/gentab/text/{outputFormat}", genTabRequest);
		if (rm.StatusCode != System.Net.HttpStatusCode.OK)
		{
			Warn($"Status code {rm.StatusCode}");
			string errorBody = await rm.Content.ReadAsStringAsync();
			Warn(errorBody);
		}
		else
		{
			string body = await rm.Content.ReadAsStringAsync();
			Verbose(body);
		}
	}

	static async Task GenJson()
	{
		// THIS IS DUPLICATE CODE AND SUBJECT TO INVESTIGATION (SEE NOTE ABOVE).
		var genTabRequest = new
		{
			name = "Report-2",
			top = topVariable,
			side = sideVariable,
			filter,
			weight,
			sProps = new
			{
				caseFilter,
				topInsert,
				sideInsert,
				level,
				initAsMissing = true,
				excludeNE = true,
				padHierarchics = true,
				arithOverStats = true
			},
			dProps = new
			{
				// TODO ????????????????????????????
			}
		};
		async Task InnerPandas(int shape)
		{
			Sep($"Generate Cross-tabulation Report for Pandas shape {shape}");
			rm = await client!.PostAsJsonAsync($"report/gentab/pandas/{shape}", genTabRequest);
			if (rm.StatusCode != System.Net.HttpStatusCode.OK)
			{
				Warn($"Status code {rm.StatusCode}");
				string errorBody = await rm.Content.ReadAsStringAsync();
				Warn(errorBody);
			}
			else
			{
				string body = await rm.Content.ReadAsStringAsync();
				Verbose(body);
			}
		}
		await InnerPandas(1);
		await InnerPandas(2);
		await InnerPandas(3);
	}

	/// <summary>
	/// Close the previously opened job. This doesn't do anything inernally at the moment, but it's
	/// here for completeness because in the future it may change counters or release resources.
	/// </summary>
	static async Task CloseJob()
	{
		Sep("Close Job");
		rm = await client!.DeleteAsync("job/close");
		doc = await CheckResponse(rm);
		bool closed = doc.RootElement.GetBoolean();
		Info($"Job closed -> {closed}");
	}

	/// <summary>
	/// Ends the session, which is important because in some services a single session usage may be enforced.
	/// </summary>
	static async Task EndSession()
	{
		Sep("End session");
		rm = await client!.DeleteAsync("session/end");
		doc = await CheckResponse(rm);
		bool success = doc.RootElement.GetBoolean();
		Info($"End session -> {success}");
	}

	#region Helpers

	static async Task<JsonDocument> CheckResponse(HttpResponseMessage rm)
	{
		if (rm.StatusCode != System.Net.HttpStatusCode.OK)
		{
			Error($"Status code {rm.StatusCode}");
			string errorBody = await rm.Content.ReadAsStringAsync();
			Fatal(errorBody);
		}
		string json = await rm.Content.ReadAsStringAsync();
		Verbose(json);
		return JsonDocument.Parse(json);
	}

	static void Pause()
	{
		if (Debugger.IsAttached)
		{
			Console.WriteLine("PAUSE...");
			Console.ReadLine();
		}
	}

	static void Sep(string title)
	{
		string bar = new string('─', title.Length + 4);
		Info("┌" + bar + "┐");
		Info("│  " + title + "  │");
		Info("└" + bar + "┘");
	}

	static void Info(string message)
	{
		Console.ForegroundColor = ConsoleColor.White;
		Console.WriteLine(message);
		Console.ResetColor();
	}

	static void Warn(string message)
	{
		Console.ForegroundColor = ConsoleColor.Magenta;
		Console.WriteLine(message);
		Console.ResetColor();
	}

	static void Verbose(string message)
	{
		Console.ForegroundColor = ConsoleColor.DarkYellow;
		Console.WriteLine(message);
		Console.ResetColor();
	}

	static void Error(string message)
	{
		Console.ForegroundColor = ConsoleColor.White;
		Console.BackgroundColor = ConsoleColor.DarkRed;
		Console.WriteLine(message);
		Console.ResetColor();
	}

	static void Fatal(string message)
	{
		Error(message);
		Pause();
		Environment.Exit(1);
	}

	#endregion
}

sealed record Cust(string Id, string Name, string? DisplayName)
{
	public List<Job> Jobs { get; } = new List<Job>();
}

sealed record Job(string Id, string Name, string? DisplayName, Cust ParentCust);
