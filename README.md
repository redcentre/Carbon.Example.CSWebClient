# Overview

This project contains an example C# command line program that calls the Carbon web service using plain JSON requests and responses. It does *not* use the [RCS.Carbon.Examples.WebService.Common][webcomn] NuGet package which can provide strong class type binding to requests and responses for .NET clients.

This example was primarily created so that developers can see the typical logical sequence of web service calls that are required to perform useful work. That sequence of endpoint calls is as follows. Note that the calls marked as optional would only be useful in a GUI application where the user could be presented with selection controls. 

<div style="padding-left:2em;">

 • `service/info`  
 *Sanity check that the service is responding and obtain metadata about the service.*
 
 • `session/start/authenticate/name`  
 *Use account name and password credentials to authenticate (login) to the service and create a session. The response contains a list of all the customers and their child jobs that the account has access to. The user is prompted to enter the job they want to open.*
 
 • `job/open`  
 *Use account name and password credentials to authenticate (login) to the service and create a session. The response contains a list of all the customers and their child jobs that the account has access to. The user is prompted in a simple contole prompt loop to enter the job they want to open.*
 
 > • `job/vartree/list` (optional)  
 *List the vartrees (Variable Trees) available in the job. A more complex app would ask the user which vartree they want to be select as active in the job.*
 
 > • `job/vartree/{vartreeName}` (optional)  
 *Sets a vartree as the active on the opened job. This example just sets the first vartree as active.*
 
 > • `job/vartree/nodes` (optional)  
 *Gets the contents of the active vartree as a hierarchy of node objects. A GUI app could display the nodes in a tree control and the user could select them to be the top and side variables for a report.*
 
 • `report/gentab/text/{outputFormat}`  
 *Generates a cross-tabulation report in a specified format using top and side variables.*

 • UNDER CONSTRUCTION  
 *A demonstation of how to generate reports as different shapes of JSON.*
 
 • `job/close`  
 *Close the previously opened job.*

 • `session/end/logoff`  
 *Ends the web service session and releases all resources created by the previous `session/start` call.*

 </div>

 ----

 ### JSON Processing

 POST request bodies are created by serializing an anonymous class containing the proeprties into a string of JSON.

 Response bodies are deserialized into a JSON string and loaded into a [JsonDocument][jsondoc] class so the property values can be accessed more easily.

 ----

 ### Command Parameters

 The example console command behaviour can be adjust using optional command line arguments.

 ```
 /b service-base-address
 /u user-name
 /p password
 /t top-variable-name
 /s side-variable-name
 /f report-filter-expression
 /w report-weight-expression
 /o report-output-format
 ```

 Example:

 ```
 CSWebClient /b https://mydomain/carbon/ /u freddy /p T0pS3cret /o html
 ```

 The currently available output formats are:

 ```
 TSV
 CSV
 SSV
 XML
 HTML
 OXT
 OXTNums
 MultiCube
 ```

 ----

 *TO BE CONTINUED...*

 [webcomn]: https://nugetprodusnc-northcentralus-01.regional.azure-api.net/packages/RCS.Carbon.Examples.WebService.Common
 [jsondoc]: https://learn.microsoft.com/en-us/dotnet/api/system.text.json.jsondocument