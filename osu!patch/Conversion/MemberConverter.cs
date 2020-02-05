﻿using dnlib.DotNet;
using osu_patch.Explorers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using osu_patch.Lib.HookGenerator;

namespace osu_patch.Conversion
{
	public class MemberConverter
	{
		private ModuleExplorer _moduleExplorer;

		public MemberConverter(ModuleExplorer moduleExplorer) =>
			_moduleExplorer = moduleExplorer;

		public MethodSig MethodInfoToMethodSig(MethodInfo methInfo, bool hasThis = false) =>
			MethodInfoToMethodSig(methInfo.ReturnType, methInfo, hasThis);

		public MethodSig MethodInfoToMethodSig(Type retType, MethodBase methBase, bool hasThis = false)
		{
			var genParamCount = methBase.ContainsGenericParameters ? methBase.GetGenericArguments().Length : 0;
			var newParams = methBase.GetParameters().Skip(hasThis ? 1 : 0).Select(x => ImportAsOsuModuleType(x.ParameterType).ToTypeSig()).ToList();
			return new MethodSig(ReflectionToDnLibConvention(methBase.CallingConvention), (uint)genParamCount, ImportAsOsuModuleType(retType).ToTypeSig(), newParams);
		}

		public IMemberRef ResolveMemberInfo(MemberInfo memberInfo)
		{
			if (memberInfo is Type type)
				return ImportAsOsuModuleType(type).ResolveTypeDef();

			var importedType = ImportAsOsuModuleType(memberInfo.DeclaringType).ResolveTypeDef();

			switch (memberInfo.MemberType)
			{
				case MemberTypes.Field:
					return importedType.IsSystemType()
						? (IMemberRef)_moduleExplorer.Module.Import((FieldInfo)memberInfo)
						: importedType.FindField(_moduleExplorer.NameProvider.GetName(memberInfo.Name));

				case MemberTypes.Constructor:
					if (importedType.IsSystemType())
						return _moduleExplorer.Module.Import((MethodBase)memberInfo);

					var ctorInfo = (ConstructorInfo)memberInfo;
					return importedType.FindMethod(ctorInfo.Name, MethodInfoToMethodSig(typeof(void), ctorInfo));

				case MemberTypes.Method:
					if (importedType.IsSystemType())
						return _moduleExplorer.Module.Import((MethodBase)memberInfo);

					var methodInfo = (MethodInfo)memberInfo;

					var name = methodInfo.IsSpecialName ? methodInfo.Name : _moduleExplorer.NameProvider.GetName(methodInfo.Name);
					return importedType.FindMethod(name, MethodInfoToMethodSig(methodInfo));

				default:
					throw new ArgumentOutOfRangeException();
			}
		}

		public CallingConvention ReflectionToDnLibConvention(CallingConventions refConv)
		{
			CallingConvention newConv = CallingConvention.Default;

			if ((refConv & CallingConventions.VarArgs) != 0)
				newConv |= CallingConvention.VarArg;

			if ((refConv & CallingConventions.Any) == CallingConventions.Any)
				newConv |= CallingConvention.VarArg | CallingConvention.Default; // BUG ??????

			if ((refConv & CallingConventions.HasThis) != 0)
				newConv |= CallingConvention.HasThis;

			if ((refConv & CallingConventions.ExplicitThis) != 0)
				newConv |= CallingConvention.ExplicitThis;

			return newConv;
		}

		/// <summary>
		/// Convert hook types (generated by HookGenerator) to osu!.exe TypeSig
		/// </summary>
		public ITypeDefOrRef ImportAsOsuModuleType(Type type)
		{
			if (type.IsSystemType() || !HookAssemblyCache.IsHookAssembly(type.Assembly)) // System types and external dependencies (OpenTK, etc)
				return _moduleExplorer.Import(type);

			// from this point we know that type argument is definitely a Type from OsuHooks assembly

			if (type.IsNested)
				return UnnestType(type);

			return _moduleExplorer[type.FullName].Type;
		}

		private TypeDef UnnestType(Type type) =>
			UnnestType(type, new List<Type>());

		private TypeDef UnnestType(Type type, List<Type> unnestOrder)
		{
			if (!type.IsNested) // finally! now unnesting
			{
				var currentTypeDef = _moduleExplorer[type.FullName];

				foreach (var nextTypeDef in unnestOrder)
					currentTypeDef = currentTypeDef.FindNestedType(nextTypeDef.Name);

				return currentTypeDef.Type;
			}

			unnestOrder.Add(type);
			return UnnestType(type.DeclaringType, unnestOrder);
		}

		static class HookAssemblyCache // this is needed
		{
			private static HashSet<string> _guidCache = new HashSet<string>();

			public static bool IsHookAssembly(Assembly ass)
			{
				var guid = ass.GetAssemblyGuid();

				if (_guidCache.Contains(guid))
					return true;

				if (ass.CustomAttributes.Any(x => x.AttributeType.Name == HookGenerator.IDENTIFICATION_ATTRIBUTE_NAME))
				{
					_guidCache.Add(guid);
					return true;
				}

				return false;
			}
		}
	}
}