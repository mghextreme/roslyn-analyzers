// � 2019 Koninklijke Philips N.V. See License.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Philips.CodeAnalysis.Common;

namespace Philips.CodeAnalysis.DuplicateCodeAnalyzer
{
	[DiagnosticAnalyzer(LanguageNames.CSharp)]
	public class AvoidDuplicateCodeAnalyzer : DiagnosticAnalyzer
	{
		private const string Title = @"Avoid Duplicate Code";
		private const string MessageFormat = @"Duplicate code found at {0}";
		private const string Description = @"Duplicate code is less maintainable";
		private const string Category = Categories.Maintainability;

		public AvoidDuplicateCodeAnalyzer()
		{

		}

		public int DefaultDuplicateTokenThreshold = 100;

		public static DiagnosticDescriptor Rule = new DiagnosticDescriptor(Helper.ToDiagnosticId(DiagnosticIds.AvoidDuplicateCode), Title, MessageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true, description: Description);
		public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get { return ImmutableArray.Create(Rule); } }

		public override void Initialize(AnalysisContext context)
		{
			context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
			context.EnableConcurrentExecution();

			context.RegisterCompilationStartAction(compilationContext =>
			{
				EditorConfigOptions options = InitializeEditorConfigOptions(compilationContext.Options.AdditionalFiles);
				HashSet<string> exceptions = new HashSet<string>();
				if (!options.IgnoreExceptionsFile)
				{
					exceptions = InitializeExceptions(compilationContext.Options.AdditionalFiles);
				}
				var compilationAnalyzer = new CompilationAnalyzer(options.TokenCount, exceptions, options.GenerateExceptionsFile);
				compilationContext.RegisterSyntaxNodeAction(compilationAnalyzer.AnalyzeMethod, SyntaxKind.MethodDeclaration);
				compilationContext.RegisterCompilationEndAction(compilationAnalyzer.EndCompilationAction);
			});
		}

		public const string ExceptionsFileName = @"DuplicateCode.Allowed.txt";

		public virtual HashSet<string> InitializeExceptions(ImmutableArray<AdditionalText> additionalFiles)
		{
			foreach (AdditionalText additionalFile in additionalFiles)
			{
				string fileName = Path.GetFileName(additionalFile.Path);
				StringComparer comparer = StringComparer.OrdinalIgnoreCase;
				if (comparer.Equals(fileName, ExceptionsFileName))
				{
					return LoadAllowedMethods(additionalFile.GetText());
				}
			}
			return new HashSet<string>();
		}

		public virtual EditorConfigOptions InitializeEditorConfigOptions(ImmutableArray<AdditionalText> additionalFiles)
		{
			EditorConfigOptions options = new EditorConfigOptions(DefaultDuplicateTokenThreshold);

			foreach (AdditionalText additionalFile in additionalFiles)
			{
				string fileName = Path.GetFileName(additionalFile.Path);
				StringComparer comparer = StringComparer.OrdinalIgnoreCase;
				if (comparer.Equals(fileName, @".editorconfig"))
				{
					LoadEditorConfigOptions(options, additionalFile.GetText());
				}
			}

			return options;
		}

		public virtual void LoadEditorConfigOptions(EditorConfigOptions options, SourceText text)
		{
			if (text == null)
			{
				return;
			}
			string diagnosticId = Helper.ToDiagnosticId(DiagnosticIds.AvoidDuplicateCode);

			foreach (TextLine textLine in text.Lines)
			{
				string line = textLine.ToString();
				if (line.Contains($@"dotnet_code_quality.{diagnosticId}.ignore_exceptions_file"))
				{
					options.IgnoreExceptionsFile = true;
				}
				if (line.Contains($@"dotnet_code_quality.{diagnosticId}.generate_exceptions_file"))
				{
					options.GenerateExceptionsFile = true;
				}
				else if (line.Contains($@"dotnet_code_quality.{diagnosticId}.token_count"))
				{
					if (line.Contains('='))
					{
						string tc = line.Substring(line.IndexOf('=') + 1);
						tc = tc.Trim();
						try
						{
							int duplicateTokenThreshold = int.Parse(tc);
							if (duplicateTokenThreshold > 100)
							{
								duplicateTokenThreshold = 100;
							}
							if (duplicateTokenThreshold < 20)
							{
								duplicateTokenThreshold = 20;
							}
							options.TokenCount = duplicateTokenThreshold;
						}
						catch (Exception) { }
					}
				}
			}
		}

		public virtual HashSet<string> LoadAllowedMethods(SourceText text)
		{
			HashSet<string> result = new HashSet<string>();
			foreach (TextLine line in text.Lines)
			{
				result.Add(line.ToString());
			}
			return result;
		}

		private class CompilationAnalyzer
		{
			private readonly DuplicateDetectorDictionary _library = new DuplicateDetectorDictionary();
			private readonly List<Diagnostic> _diagnostics = new List<Diagnostic>();
			private readonly int _duplicateTokenThreshold;
			private readonly HashSet<string> _exceptions;
			private readonly bool _generateExceptionsFile;

			public CompilationAnalyzer(int duplicateTokenThreshold, HashSet<string> exceptions, bool generateExceptionsFile)
			{
				_duplicateTokenThreshold = duplicateTokenThreshold;
				_exceptions = exceptions;
				_generateExceptionsFile = generateExceptionsFile;
			}

			public string ToPrettyReference(FileLinePositionSpan fileSpan)
			{
				string file = Path.GetFileName(fileSpan.Path);
				// This API uses 0-based line positioning, so add 1
				return $@"{file} line {fileSpan.StartLinePosition.Line + 1}";
			}

			public void AnalyzeMethod(SyntaxNodeAnalysisContext obj)
			{
				MethodDeclarationSyntax methodDeclarationSyntax = (MethodDeclarationSyntax)obj.Node;
				SyntaxNode body = methodDeclarationSyntax.Body;
				if (body == null)
				{
					return;
				}

				if (_exceptions.Contains(methodDeclarationSyntax.Identifier.ValueText))
				{
					return;
				}

				RollingTokenSet rollingTokenSet = new RollingTokenSet(_duplicateTokenThreshold);

				foreach (SyntaxToken token in body.DescendantTokens())
				{
					// For every set of token_count contiguous tokens, create a hash and add it to a dictionary with some evidence.
					(int hash, Evidence evidence) = rollingTokenSet.Add(new TokenInfo(token));

					if (rollingTokenSet.IsFull())
					{
						Evidence existingEvidence = _library.TryAdd(hash, evidence);
						if (existingEvidence != null)
						{

							Location location = evidence.LocationEnvelope.Contents();
							Location existingEvidenceLocation = existingEvidence.LocationEnvelope.Contents();

							// We found a duplicate, but if it's partially duplicated with itself, ignore it.
							if (!location.SourceSpan.IntersectsWith(existingEvidenceLocation.SourceSpan))
							{
								string reference = ToPrettyReference(existingEvidenceLocation.GetLineSpan());
								_diagnostics.Add(Diagnostic.Create(Rule, location, new List<Location>() { existingEvidenceLocation }, reference));

								if (_generateExceptionsFile)
								{
									File.AppendAllText(@"DuplicateCode.Allowed.GENERATED.txt", methodDeclarationSyntax.Identifier.ValueText + Environment.NewLine);
								}

								// Don't pile on.  Move on to the next method.
								return;
							}

							// Don't pile on.  Move on to the next method.
							return;
						}
					}
				}
			}

			public void EndCompilationAction(CompilationAnalysisContext context)
			{
				foreach (Diagnostic diagnostic in _diagnostics)
				{
					context.ReportDiagnostic(diagnostic);
				}
			}
		}
	}

	public class EditorConfigOptions
	{
		public EditorConfigOptions(int defaultTokenCount)
		{
			TokenCount = defaultTokenCount;
		}

		public int TokenCount { get; set; } = 0;
		public bool IgnoreExceptionsFile { get; set; } = false;
		public bool GenerateExceptionsFile { get; set; } = false;
	}

	public class DuplicateDetectorDictionary
	{
		private readonly Dictionary<int, List<Evidence>> _library = new Dictionary<int, List<Evidence>>();
		private readonly object _lock = new object();

		public Evidence TryAdd(int key, Evidence value)
		{
			lock (_lock)
			{
				if (_library.TryGetValue(key, out List<Evidence> existingValues))
				{
					// We found a potential duplicate.  Is it actually?
					foreach (Evidence e in existingValues)
					{
						if (e.Components.SequenceEqual(value.Components))
						{
							// Yes, just return the duplicate information
							return e;
						}
					}
					// Our key exists already, but not us.  I.e., a hash collision.
					existingValues.Add(value);
					return null;
				}

				// New key
				existingValues = new List<Evidence>();
				existingValues.Add(value);
				_library.Add(key, existingValues);
				return null;
			}
		}
	}

	public class Evidence
	{
		public LocationEnvelope LocationEnvelope { get; set; } = null;
		public List<int> Components { get; set; } = new List<int>();
	}

	public class LocationEnvelope
	{
		private Location _location = null;
		public LocationEnvelope(Location location = null)
		{
			_location = location;
		}
		public virtual Location Contents()
		{
			return _location;
		}
	}


	public class TokenInfo
	{
		private readonly SyntaxToken _syntaxToken;
		private readonly int _hash = 0;

		public TokenInfo(int hash)
		{
			_hash = hash;
		}

		public TokenInfo(SyntaxToken syntaxToken)
		{
			_syntaxToken = syntaxToken;

			// SyntaxKinds start at 8193.
			_hash = ((int)_syntaxToken.Kind()) - 8192;
		}

		public virtual LocationEnvelope GetLocationEnvelope()
		{
			return new LocationEnvelope(_syntaxToken.GetLocation());
		}

		public virtual SyntaxTree GetSyntaxTree()
		{
			return _syntaxToken.SyntaxTree;
		}

		public override int GetHashCode()
		{
			return _hash;
		}
	}

	public class RollingHashCalculator<T>
	{
		protected Queue<T> _components = new Queue<T>();
		private readonly int _basePowMaxComponentsModulusCache;
		private readonly int _base;
		private readonly int _modulus;

		public RollingHashCalculator(int maxItems, int baseModulus = 2048, int modulus = 1723)
		{
			MaxItems = maxItems;
			_base = baseModulus;
			_modulus = modulus;

			_basePowMaxComponentsModulusCache = _base;
			_basePowMaxComponentsModulusCache %= _modulus;
			for (int i = 1; i < MaxItems; i++)
			{
				_basePowMaxComponentsModulusCache *= _base;
				_basePowMaxComponentsModulusCache %= _modulus;
			}
		}

		public Queue<T> Components { get { return _components; } }

		public List<int> ToComponentHashes()
		{
			var componentHashes = new List<int>();
			foreach (T token in Components)
			{
				componentHashes.Add(token.GetHashCode());
			}
			return componentHashes;
		}

		public int MaxItems { get; private set; }
		public int HashCode { get; protected set; }

		public bool IsFull()
		{
			return _components.Count >= MaxItems;
		}

		public T Add(T token)
		{
			_components.Enqueue(token);
			T purgedHashComponent = default;
			bool isPurged = false;
			if (_components.Count > MaxItems)
			{
				// Remove old value
				purgedHashComponent = _components.Dequeue();
				isPurged = true;
			}
			CalcNewHashCode(token, isPurged, purgedHashComponent);
			return _components.Peek();
		}

		protected virtual void CalcNewHashCode(T hashComponent, bool isPurged, T purgedHashComponent)
		{
			// Shift left
			HashCode *= _base;
			HashCode %= _modulus;

			// Add new value
			HashCode += hashComponent.GetHashCode();
			HashCode %= _modulus;

			if (isPurged)
			{
				// Remove old value
				HashCode = (HashCode - ((purgedHashComponent.GetHashCode() * _basePowMaxComponentsModulusCache) % _modulus) + _modulus) % _modulus;
			}
		}
	}

	public class RollingTokenSet
	{
		private readonly RollingHashCalculator<TokenInfo> _hashCalculator;

		public RollingTokenSet(int maxItems)
			: this(new RollingHashCalculator<TokenInfo>(maxItems))
		{ }

		public RollingTokenSet(RollingHashCalculator<TokenInfo> hashCalculator)
		{
			_hashCalculator = hashCalculator;
		}

		public virtual LocationEnvelope MakeFullLocationEnvelope(TokenInfo firstToken, TokenInfo lastToken)
		{
			if (firstToken.GetLocationEnvelope().Contents() == null)
			{
				return firstToken.GetLocationEnvelope();
			}

			int start = firstToken.GetLocationEnvelope().Contents().SourceSpan.Start;
			int end = lastToken.GetLocationEnvelope().Contents().SourceSpan.End;
			TextSpan textSpan = TextSpan.FromBounds(start, end);
			Location location = Location.Create(firstToken.GetSyntaxTree(), textSpan);
			return new LocationEnvelope(location);
		}

		public (int, Evidence) Add(TokenInfo token)
		{
			TokenInfo firstToken = _hashCalculator.Add(token);
			Evidence e = new Evidence();
			e.Components = _hashCalculator.ToComponentHashes();
			e.LocationEnvelope = MakeFullLocationEnvelope(firstToken, token);
			return (_hashCalculator.HashCode, e);
		}

		public bool IsFull()
		{
			return _hashCalculator.IsFull();
		}
	}
}
