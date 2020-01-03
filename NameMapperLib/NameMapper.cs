﻿using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using NameMapperLib.Exceptions;

using OsuPatchCommon;

namespace NameMapperLib
{
	/// <summary>
	/// This program is used to identify obfuscated method/field/class names considering that you have similar binary (may be updated/changed, comparisons are made based on similarity).
	/// </summary>
	public class NameMapper
	{
		private const float DEFAULT_MINIMAL_SUCCESS_PERCENTAGE = .1f;

		internal ModuleDefMD CleanModule { get; } // Module to inherit names from

		internal ModuleDefMD ObfModule { get; }
		
		private TextWriter _debugOutput;

		private NamableProcessor _namableProcessor;

		internal ConcurrentDictionary<string, string> NamePairs = new ConcurrentDictionary<string, string>();

		public bool DeobfuscateNames { get; }

		private int _overallErroredMethods;

		private int _inWork;

		public bool Processed { get; private set; }

		public bool ShowErroredMethods { get; set; } = true;

		public NameMapper(ModuleDefMD cleanModule, ModuleDefMD obfModule, TextWriter debugOutput = null, bool deobfuscateNames = true)
		{
			CleanModule = cleanModule;
			ObfModule = obfModule;
			DeobfuscateNames = deobfuscateNames;
			_debugOutput = debugOutput;
			_namableProcessor = new NamableProcessor(this);
		}

		public Dictionary<string, string> GetNamePairs() => new Dictionary<string, string>(NamePairs);

		public void BeginProcessing()
		{
			if (Processed)
				throw new NameMapperProcessingException("Already processed! This class is a one-time use.");

			Processed = true;

			// -- BEGIN IDENTIFYING PROCESS

			//	 -- Identifying using entry point as start.

			var cleanEntry = FindEntryPoint(CleanModule);
			var obfEntry = FindEntryPoint(ObfModule);

			Message("I | Calling recurse! Level: 0");

			EnqueueRecurseThread(cleanEntry, obfEntry);

			WaitMakeSure();

			//	 -- 

			//	 -- Identifying using already known type pairs.

			Message("I | Now identifying methods in already known type pairs.");

			int prevCount = -1;

			var procMan = _namableProcessor.Processed;

			while (true)
			{
				long recurseNum = 0;

				foreach (var item in procMan.AlreadyProcessedTypes.Where(x => !x.Types.Item2.IsEazInternalName()))
				{
					if (item.FullyProcessed)
						continue;

					var cleanMethods = item.Types.Item1.ScopeType.ResolveTypeDef()?.Methods.Where(x => !x.IsEazInternalName()).ToList();
					var obfMethods = item.Types.Item2.ScopeType.ResolveTypeDef()?.Methods.Where(x => !x.IsEazInternalName()).ToList();

					if (cleanMethods is null || obfMethods is null)
						continue;

					List<MethodDef> cleanUniqueMethods = cleanMethods.ExcludeMethodsDuplicatesByOpcodes(); // exclude duplicates
					List<MethodDef> obfUniqueMethods = obfMethods.ExcludeMethodsDuplicatesByOpcodes();

					foreach (var cleanMethod in cleanUniqueMethods)
					{
						var obfMethod = obfUniqueMethods.FirstOrDefault(x => AreOpcodesEqual(cleanMethod?.Body?.Instructions, x.Body?.Instructions));

						if (obfMethod != null)
						{
							EnqueueRecurseThread(cleanMethod, obfMethod, recurseNum);
							recurseNum += 1000000000;
						}
					}

					item.FullyProcessed = true;
				}

				WaitMakeSure();

				int count = procMan.AlreadyProcessedTypes.Count;

				if (count == prevCount)
					break;

				Message($"I | {count - prevCount} new types! Processing...");

				prevCount = count;
			}

			//	 --

			WaitMakeSure();

			if (_overallErroredMethods > 0)
				Message($"W | Not all methods are processed! {_overallErroredMethods} left behind.");

			Message($"I | Overall known classes: {procMan.AlreadyProcessedTypes.Count}; Fully processed classes: {procMan.AlreadyProcessedTypes.Count(x => x.FullyProcessed)}");

			var processedTypesCount = procMan.AlreadyProcessedTypes.Count;
			var allTypesCount = ObfModule.CountTypes(x => !x.IsEazInternalName());
			var processedPercentage = (float)processedTypesCount / allTypesCount;

			if (processedPercentage < DEFAULT_MINIMAL_SUCCESS_PERCENTAGE)
				throw new NameMapperProcessingException($"Processed types percentage: {processedTypesCount}/{allTypesCount} => {processedPercentage * 100}% < {DEFAULT_MINIMAL_SUCCESS_PERCENTAGE * 100}% (min), counting as unsuccessful.");

			// -- END
		}

		public string FindName(string cleanName)
		{
			string obfName = null;

			if (Processed)
				if (!NamePairs.TryGetValue(cleanName, out obfName))
					throw new NameMapperException("Unable to find specified name: " + cleanName);

			return obfName;
		}

		private void WaitMakeSure()
		{
			int occ = 0;

			long prevState = 0;

			while (true)
			{
				if (occ < 3 && _inWork == 0)
					occ++;
				else if (occ >= 3)
					break;
				else
					occ = 0;

				Thread.Sleep(100);

				if (Math.Abs(prevState - _inWork) > 25)
					Message("I | Waiting far all threads to finish! In work: " + _inWork);

				prevState = _inWork;
			}
		}

		/// <summary>
		/// Try to find a valid entry point for assembly, returns null if not found.
		/// </summary>
		/// <param name="module">Module to find entry point in.</param>
		/// <returns>True entry point, null if not found.</returns>
		private MethodDef FindEntryPoint(ModuleDef module)
		{
			if (module?.EntryPoint?.Body?.Instructions?.Count == 2 && module.EntryPoint.Body.Instructions[0]?.OpCode == OpCodes.Call)
				return ((IMethodDefOrRef)module.EntryPoint.Body.Instructions[0]?.Operand).ResolveMethodDef();

			throw new NameMapperException($"Unable to find entry point of given module");
		}

		private void EnqueueRecurseThread(IMethodDefOrRef cleanMethod, IMethodDefOrRef obfMethod, long recurseLevel = 0) => EnqueueRecurseThread(cleanMethod?.ResolveMethodDef(), obfMethod?.ResolveMethodDef(), recurseLevel);

		private void EnqueueRecurseThread(MethodDef cleanMethod, MethodDef obfMethod, long recurseLevel = 0)
		{
			Interlocked.Increment(ref _inWork);

			ThreadPool.QueueUserWorkItem(state =>
			{
				try
				{
					var recurseResult = new RecurseResult(RecurseResultEnum.None);

					try
					{
						recurseResult = RecurseFromMethod(cleanMethod, obfMethod, recurseLevel++);
					}
					catch (Exception e)
					{
						Message($"E | An error occurred while trying to recurse level-{recurseLevel} method. Details:\n{e}");
					}

					lock (_msgLock)
					{
						if (ShowErroredMethods &&
							recurseResult.Result != RecurseResultEnum.NullArguments &&
							recurseResult.Result != RecurseResultEnum.InProcess &&
							recurseResult.Result != RecurseResultEnum.Ok)
						{
							var recurseStr = recurseLevel >= 1000000000 ? $"{recurseLevel / 1000000000}-{recurseLevel % 1000000000}" : recurseLevel.ToString();

							Message($"I | [{recurseStr}] Done! {cleanMethod.FullName}; Result: ", false);

							var prevColor = Console.ForegroundColor;
							Console.ForegroundColor = ConsoleColor.Red;

							Message($"{recurseResult.Result}", recurseResult.Difference == 0);

							Console.ForegroundColor = prevColor;

							if (recurseResult.Difference != 0)
								Message("; Difference: " + recurseResult.Difference);

							if (recurseResult.Result != RecurseResultEnum.Ok)
								_overallErroredMethods++;
						}
					}
				}
				finally
				{
					Interlocked.Decrement(ref _inWork);
				}
			});
		}

		/// <summary>
		/// Start search recurse. Will use EnqueueRecurseThread.
		/// </summary>
		/// <param name="cleanMethod">Method in clean assembly to start recurse from.</param>
		/// <param name="obfMethod">Method in obfuscated assembly to start recurse from.</param>
		/// <param name="recurseLevel">Level of recurse (always start with 0).</param>
		/// <returns>Result of recurse operation.</returns>
		private RecurseResult RecurseFromMethod(MethodDef cleanMethod, MethodDef obfMethod, long recurseLevel)
		{
			if (cleanMethod is null || obfMethod is null)
				return new RecurseResult(RecurseResultEnum.NullArguments);

			if (Monitor.TryEnter(obfMethod)) // clean is used in OperandProcessors.ProcessMethod, hardcoded but that's important
			{
				try
				{
					if (_namableProcessor.ProcessMethod(cleanMethod, obfMethod) != ProcessResult.Ok)
						return new RecurseResult(RecurseResultEnum.Ok); // may be framework type/already in process/different methods etc.

					if(obfMethod.DeclaringType.Name == "CommonUpdater" && obfMethod.Name == "doUpdate")
						Message();

					var cleanInstr = cleanMethod.Body?.Instructions;
					var obfInstr = obfMethod.Body?.Instructions;

					if (!cleanMethod.HasBody || !obfMethod.HasBody)
						return new RecurseResult(RecurseResultEnum.Ok); // all possible things are done at this moment

					// ReSharper disable PossibleNullReferenceException
					if (cleanInstr.Count != obfInstr.Count)
						return new RecurseResult(RecurseResultEnum.DifferentInstructionsCount, Math.Abs(cleanInstr.Count - obfInstr.Count));

					if (!AreOpcodesEqual(cleanInstr, obfInstr))
						return new RecurseResult(RecurseResultEnum.DifferentInstructions);

					for (int i = 0; i < cleanInstr.Count; i++)
					{
						object cleanOperand = cleanInstr[i].Operand;
						object obfOperand = obfInstr[i].Operand;

						if (cleanOperand is null || obfOperand is null)
							continue;

						if (cleanOperand.GetType() != obfOperand.GetType())
							continue;

						/*if (cleanOperand is MemberRef cleanRef && obfOperand is MemberRef obfRef)
						{
							if(cleanRef.IsFieldRef != obfRef.IsFieldRef)
								continue;

							if (cleanMethod.DeclaringType.Name == "SynchronizedDictionary`2" && cleanMethod.Name == "get_Count")
								Message();

							if (cleanRef.IsFieldRef)
							{
								cleanOperand = cleanRef.ResolveField();
								obfOperand = obfRef.ResolveField();
							}
							else
							{
								cleanOperand = cleanRef.ResolveMethod();
								obfOperand = obfRef.ResolveMethod();
							}

							if (cleanOperand is null || obfOperand is null)
								continue;
						}*/

						if (cleanOperand is IMethodDefOrRef)
							EnqueueRecurseThread(cleanOperand as IMethodDefOrRef, obfOperand as IMethodDefOrRef, recurseLevel + 1);
						else if (cleanOperand is IType)
							_namableProcessor.ProcessType(cleanOperand as IType, obfOperand as IType);
						else if (cleanOperand is FieldDef)
							_namableProcessor.ProcessField(cleanOperand as FieldDef, obfOperand as FieldDef);
					}
				}
				finally
				{
					Monitor.Exit(obfMethod);
				}
			}
			else return new RecurseResult(RecurseResultEnum.InProcess);

			return new RecurseResult(RecurseResultEnum.Ok);
		}

		/// <summary>
		/// Check instruction equality using opcodes only, no operands used.
		/// </summary>
		/// <returns>Are opcodes equal or not</returns>
		private bool AreOpcodesEqual(IList<Instruction> cleanInstructions, IList<Instruction> obfInstructions)
		{
			if (cleanInstructions is null || obfInstructions is null)
				return false;

			if (cleanInstructions.Count != obfInstructions.Count)
				return false;

			for (int i = 0; i < cleanInstructions.Count; i++)
			{
				var cleanOpcode = cleanInstructions[i].OpCode;
				var obfOpcode = obfInstructions[i].OpCode;

				if (cleanOpcode != obfOpcode)
					return false;

				var cleanOperand = cleanInstructions[i].Operand;
				var obfOperand = obfInstructions[i].Operand;

				/* // this doesn't really add any improvement in ExcludeMethodsDuplicatesByOpcodes as i expected, only REALLY slows down mapping speed
				if (cleanOperand is null || obfOperand is null)
					continue;

				if (cleanOperand.GetType() != obfOperand.GetType())
					return false;

				if (cleanOperand is IMethod)
				{
					var cleanMethod = cleanOperand as IMethod;
					var obfMethod = obfOperand as IMethod;

					var expectedObfMethod = _namableProcessor.AlreadyProcessedMethods.FirstOrDefault(x => x.Item1.MDToken == cleanMethod.MDToken)?.Item2;

					if (expectedObfMethod != null && obfMethod.MDToken != expectedObfMethod.MDToken)
						return false;
				}
				else if (cleanOperand is IType)
				{
					var cleanType = cleanOperand as IType;
					var obfType = obfOperand as IType;

					var expectedObfType = _namableProcessor.AlreadyProcessedTypes.FirstOrDefault(x => x.Types.Item1.MDToken == cleanType.MDToken)?.Types.Item2;

					if (expectedObfType != null && obfType.MDToken != expectedObfType.MDToken)
						return false;
				}
				else if (cleanOperand is FieldDef)
				{
					var cleanField = cleanOperand as FieldDef;
					var obfField = obfOperand as FieldDef;

					var expectedObfField = _namableProcessor.AlreadyProcessedFields.FirstOrDefault(x => x.Item1.MDToken == cleanField.MDToken)?.Item2;

					if (expectedObfField != null && obfField.MDToken != expectedObfField.MDToken)
						return false;
				}
				*/

				/*if (cleanOperand is null || obfOperand is null || cleanOperand.GetType() != obfOperand.GetType())
					continue; // ???????

				if(cleanOperand is sbyte || cleanOperand is int || cleanOperand is float || cleanOperand is double || cleanOperand is long)
					if (!cleanOperand.Equals(obfOperand))
						return false;*/ // useless anyways (?)
			}

			return true;
		}

		private static object _msgLock = new object();

		internal bool Message(string msg = "", bool newline = true)
		{
			lock (_msgLock)
				_debugOutput?.Write(msg + (newline ? Environment.NewLine : string.Empty));

			return false;
		}

		private struct RecurseResult
		{
			public RecurseResultEnum Result;
			public int Difference;

			public RecurseResult(RecurseResultEnum result, int diff = 0)
			{
				Result = result;
				Difference = diff;
			}
		}

		private enum RecurseResultEnum
		{
			None,
			Ok,
			NullArguments,
			InProcess,
			DifferentInstructionsCount,
			DifferentInstructions,
			DifferentMethods,
		}
	}
}