using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace QSEvaluator
{
	public class QsEvaluator : IDisposable
	{

		#region strings


		private const string DriverTemplate = @"
# pragma warning disable CS0184

using Microsoft.Quantum.Simulation.Core;         
using Microsoft.Quantum.Simulation.Simulators;   
using System.Threading.Tasks;
using System;
namespace __QSI
{
	public class Driver
	{
		public static async Task Main(string[] args)
		{
			var result = await !!NAME!!.Run(new QuantumSimulator());
			if (!(result is QVoid)) {
				Console.WriteLine(result);
			}
		}
	}
}                                              
";

		private const string EmptyDriver = @"
namespace __QSI
{
	public class Driver
	{
		public static void Main() {}
	}
}
";

		private const string OperationTemplate = @"
	operation __QSI_MAIN () : ()
	{
		body
		!!BLOCK!!
	}
";

		#endregion strings

		public QsEvaluator()
		{
			const string chars =
				"abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ1234567890";

			IEnumerable<char> RndChars()
			{
				foreach (var i in Path.GetTempPath())
					yield return i;

				var rnd = new Random();
				for (var i = 0; i < 10; i++)
					yield return chars[rnd.Next(chars.Length)];

				yield return Path.DirectorySeparatorChar;
			}

			WorkingDirectory = string.Concat(RndChars());
			Directory.CreateDirectory(WorkingDirectory);

			try
			{
				var ec = RunProcess(
					"dotnet",
					arguments: $@"new console -o {ProjectDir} -lang Q#",
					stdout: out _,
					stderr: out _
				);
				var dir = Environment.CurrentDirectory;
				Environment.CurrentDirectory = ProjectDir;
				ec |= RunProcess(
					"dotnet",
					arguments: "restore",
					stdout: out _,
					stderr: out _
				);
				Environment.CurrentDirectory = dir;
				if (ec != 0)
				{
					Console.Error.WriteLine("Could not initialize Q# project.");
					Console.Error.WriteLine("Make sure the QDK is installed.");
					throw new Exception("Could not create project.");
				}

				File.Delete(path: $"{ProjectDir}Operation.qs");
				File.Delete(path: $"{ProjectDir}Driver.cs");
			}
			catch (Win32Exception)
			{
				Console.Error.WriteLine("Could not start process.");
				Console.Error.WriteLine(
					value: "Make sure .NET Core is installed and dotnet"
					       + "is available from path."
				);
			}
		}

		private string WorkingDirectory { get; }
		private string ProjectDir => $"{WorkingDirectory}__QSI{Path.DirectorySeparatorChar}";

		public void Dispose()
		{
			try
			{
				Directory.Delete(WorkingDirectory, recursive: true);
			}
			catch
			{
				// ignored
			}
		}

		~QsEvaluator() { Dispose(); }

		private static int RunProcess(
			string name,
			string arguments,
			out string stdout,
			out string stderr
		)
		{
			var output = new StringBuilder();
			var error = new StringBuilder();

			var proc = new Process
			{
				StartInfo = new ProcessStartInfo(
					name, arguments
				)
				{
					UseShellExecute = false,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					RedirectStandardInput = true
				}
			};
			proc.OutputDataReceived += (sender, args) =>
				output.AppendLine(args.Data);
			proc.ErrorDataReceived += (sender, args) =>
				error.AppendLine(args.Data);
			proc.Start();
			proc.StandardInput.Close();
			proc.BeginOutputReadLine();
			proc.BeginErrorReadLine();


			proc.WaitForExit();

			stdout = output.ToString();
			stderr = error.ToString();
			return proc.ExitCode;
		}

		private static string CleanupString(string code)
		{
			while (Regex.IsMatch(code, @"^(?:\s|/)"))
				code = Regex.Replace(code, @"^(?:\s+|//.*?\r?\n)", "");

			return code;
		}



		private bool SaveOperation(string code, out string type, out string name, out string error)
		{
			if (code.StartsWith("function"))
				type = "function";
			else if (code.StartsWith("operation"))
				type = "operation";
			else
				throw new InvalidOperationException("Code must be function or operation.");

			name = new Regex(@"^\w+")
				.Match(
					input: CleanupString(
						code: Regex.Replace(code, @"^(?:function|operation)", "")
					)
				).Value;
			var filename = $"{ProjectDir}{name}.qs";
			if (File.Exists(filename))
			{
				if (File.Exists(path: $"{filename}.backup"))
					File.Delete(path: $"{filename}.backup");

				File.Copy(filename, destFileName: $"{filename}.backup");
				File.Delete(filename);
			}


			using (var file = File.AppendText(filename))
			{
				file.WriteLine("namespace __QSI {");
				file.WriteLine("open Microsoft.Quantum.Primitive;");
				file.WriteLine("open Microsoft.Quantum.Canon;");
				file.WriteLine(code);
				file.WriteLine("}");
			}

			File.WriteAllText(path: $"{ProjectDir}Driver.cs", contents: EmptyDriver);

			var ec = RunProcess(
				"dotnet", arguments: $"build -v q {ProjectDir}__QSI.csproj", stdout: out var buildOut,
				stderr: out var buildErr
			);

			error = buildOut + buildErr;

			if (ec == 0)
				return true;

			File.Delete(filename);

			if (!File.Exists(path: $"{filename}.backup"))
				return false;
			File.Copy(sourceFileName: $"{filename}.backup", destFileName: filename);
			File.Delete(path: $"{filename}.backup");

			return false;
		}

		private void RunOperation(string code, out string output, out string error)
		{
			if (!code.StartsWith("%"))
				throw new ArgumentException(message: $"Value of {nameof(code)} must start with `%'");

			code = Regex.Replace(code, @"^%\s*", "");
			var match = Regex.Match(code, @"^(\w+)\s*");
			var name = match.Groups[1].Value;

			//todo parse args

			if (string.IsNullOrWhiteSpace(name))
				throw new InvalidOperationException(
					"Could not detect name of function/operation"
				);


			var filename = $"{ProjectDir}Driver.cs";
			if (File.Exists(filename))
				File.Delete(filename);

			File.WriteAllText(
				filename,
				contents: DriverTemplate.Replace("!!NAME!!", name)
			);

			RunProcess(
				"dotnet",
				arguments: $"run -verbosity quiet -p {ProjectDir}__QSI.csproj",
				stdout: out output,
				stderr: out error
			);

			File.Delete(filename);
		}

		private void RunBlock(string block, out string output, out string error)
		{
			var operation = CleanupString(OperationTemplate.Replace("!!BLOCK!!", block));
			if (!SaveOperation(operation, out _, out _, out var buildError))
			{
				error = buildError;
				output = "";
				return;
			}
			RunOperation("% __QSI_MAIN", out output, out error);
		}


		public EvaluationResult EvaluateStatement(string code)
		{
			code = CleanupString(code);
			if (code.StartsWith("namespace"))
				return ("", "Do not supply a namespace for operations and functions.");

			if (!Regex.IsMatch(code, @"[{;]|(^(function|operation|%))", RegexOptions.ExplicitCapture))
				return ("", "Supplied code must be either "
						+ "be a series of Q# statements,\n"
					    + "start with `operation' or `function',\n"
					    + "or be in the form `% [operation/function name]'");

			if (code.StartsWith("function") || code.StartsWith("operation"))
			{
				var success = SaveOperation(
					code,
					type: out var type,
					name: out var name,
					error: out var buildError
				);
				return success ? ($"Saved {type} `{name}'", "") : ("", buildError);
			}

			string output, error;

			if (code.StartsWith("%"))
			{
				RunOperation(code, out output, out error);
			}
			else
			{
				RunBlock("{\n" + code + "\n}", out output, out error);
			}
			return (output, error);
		}

		[SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
		public struct EvaluationResult
		{
			public readonly string Output;
			public readonly string ErrOutput;

			public void Deconstruct(out string output, out string error)
			{
				output = Output;
				error = ErrOutput;
			}

			public EvaluationResult(string output, string errOutput)
			{
				Output = (output ?? "").TrimEnd('\r', '\n');
				ErrOutput = (errOutput ?? "").TrimEnd('\r', '\n');
			}

			public static implicit operator EvaluationResult((string output, string error) tuple)
			{
				return new EvaluationResult(tuple.output, tuple.error);
			}
		}
	}
}
