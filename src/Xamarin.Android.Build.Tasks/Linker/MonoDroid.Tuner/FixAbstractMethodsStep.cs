using System;
using System.Collections.Generic;
using System.Linq;

using Mono.Cecil;

using Java.Interop.Tools.Cecil;

using Mono.Linker;
using Mono.Linker.Steps;

using Mono.Tuner;
#if NET5_LINKER
using Microsoft.Android.Sdk.ILLink;
#endif

namespace MonoDroid.Tuner
{
	/// <summary>
	/// NOTE: this step is subclassed so it can be called directly from Xamarin.Android.Build.Tasks
	/// </summary>
	public class FixAbstractMethodsStep : BaseStep
	{
		readonly TypeDefinitionCache cache;

		public FixAbstractMethodsStep (TypeDefinitionCache cache)
		{
			this.cache = cache;
		}

		protected override void ProcessAssembly (AssemblyDefinition assembly)
		{
			if (!Annotations.HasAction (assembly))
				Annotations.SetAction (assembly, AssemblyAction.Skip);

			if (IsProductOrSdkAssembly (assembly))
				return;

#if !NET5_LINKER
			CheckAppDomainUsageUnconditional (assembly, (string msg) => Context.LogMessage (MessageImportance.High, msg));
#endif

			if (FixAbstractMethodsUnconditional (assembly)) {
#if !NET5_LINKER
				Context.SafeReadSymbols (assembly);
#endif
				AssemblyAction action = Annotations.HasAction (assembly) ? Annotations.GetAction (assembly) : AssemblyAction.Skip;
				if (action == AssemblyAction.Skip || action == AssemblyAction.Copy || action == AssemblyAction.Delete)
					Annotations.SetAction (assembly, AssemblyAction.Save);
				var td = AbstractMethodErrorConstructor.DeclaringType.Resolve ();
				Annotations.Mark (td);
				Annotations.SetPreserve (td, TypePreserve.Nothing);
				Annotations.AddPreservedMethod (td, AbstractMethodErrorConstructor.Resolve ());
			}
		}


#if !NET5_LINKER
		internal void CheckAppDomainUsage (AssemblyDefinition assembly, Action<string> warn)
		{
			if (IsProductOrSdkAssembly (assembly))
				return;

			CheckAppDomainUsageUnconditional (assembly, warn);
		}

		void CheckAppDomainUsageUnconditional (AssemblyDefinition assembly, Action<string> warn)
		{
			if (!assembly.MainModule.HasTypeReference ("System.AppDomain"))
				return;

			foreach (var mr in assembly.MainModule.GetMemberReferences ()) {
				if (mr.ToString ().Contains ("System.AppDomain System.AppDomain::CreateDomain")) {
					warn (string.Format ("warning XA2000: " + Xamarin.Android.Tasks.Properties.Resources.XA2000, assembly));
					break;
				}
			}
		}
#endif

		internal bool FixAbstractMethods (AssemblyDefinition assembly)
		{
			return !IsProductOrSdkAssembly (assembly) && FixAbstractMethodsUnconditional (assembly);
		}

		bool FixAbstractMethodsUnconditional (AssemblyDefinition assembly)
		{
			if (!assembly.MainModule.HasTypeReference ("Java.Lang.Object"))
				return false;

			bool changed = false;
			foreach (var type in assembly.MainModule.Types) {
				if (MightNeedFix (type))
					changed |= FixAbstractMethods (type);
			}
			return changed;
		}

		bool IsProductOrSdkAssembly (AssemblyDefinition assembly)
		{
			return Profile.IsSdkAssembly (assembly) || Profile.IsProductAssembly (assembly);
		}

		bool MightNeedFix (TypeDefinition type)
		{
			return !type.IsAbstract && type.IsSubclassOf ("Java.Lang.Object", cache);
		}

		static bool CompareTypes (TypeReference iType, TypeReference tType)
		{
			if (iType.IsGenericParameter)
				return true;

			if (iType.IsArray) {
				if (!tType.IsArray)
					return false;
				return CompareTypes (iType.GetElementType (), tType.GetElementType ());
			}

			if (iType.IsByReference) {
				if (!tType.IsByReference)
					return false;
				return CompareTypes (iType.GetElementType (), tType.GetElementType ());
			}

			if (iType.Name != tType.Name)
				return false;

			if (iType.Namespace != tType.Namespace)
				return false;

			TypeDefinition iTypeDef = iType.Resolve ();
			if (iTypeDef == null)
				return false;

			TypeDefinition tTypeDef = tType.Resolve ();
			if (tTypeDef == null)
				return false;

			if (iTypeDef.Module.FullyQualifiedName != tTypeDef.Module.FullyQualifiedName)
				return false;

			if (iType is Mono.Cecil.GenericInstanceType && tType is Mono.Cecil.GenericInstanceType) {
				GenericInstanceType iGType = iType as GenericInstanceType;
				GenericInstanceType tGType = tType as GenericInstanceType;

				if (iGType.GenericArguments.Count != tGType.GenericArguments.Count)
					return false;
				for (int i = 0; i < iGType.GenericArguments.Count; i++) {
					if (iGType.GenericArguments [i].IsGenericParameter)
						continue;
					if (!CompareTypes (iGType.GenericArguments [i], tGType.GenericArguments [i]))
						return false;
				}
			}

			return true;
		}

		bool IsInOverrides (MethodDefinition iMethod, MethodDefinition tMethod)
		{
			if (!tMethod.HasOverrides)
				return false;

			foreach (var o in tMethod.Overrides)
				if (o != null && iMethod == o.Resolve ())
					return true;

			return false;
		}

		bool HaveSameSignature (TypeReference iface, MethodDefinition iMethod, MethodDefinition tMethod)
		{
			if (IsInOverrides (iMethod, tMethod))
				return true;

			if (iMethod.Name != tMethod.Name)
				return false;

			if (!CompareTypes (iMethod.MethodReturnType.ReturnType, tMethod.MethodReturnType.ReturnType))
				return false;

			if (iMethod.Parameters.Count != tMethod.Parameters.Count || iMethod.GenericParameters.Count != tMethod.GenericParameters.Count)
				return false;

			if (iMethod.HasParameters) {
				List<ParameterDefinition> m1p = new List<ParameterDefinition> (iMethod.Parameters);
				List<ParameterDefinition> m2p = new List<ParameterDefinition> (tMethod.Parameters);

				for (int i = 0; i < m1p.Count; i++) {
					if (!CompareTypes (m1p [i].ParameterType,  m2p [i].ParameterType))
						return false;
				}
			}

			if (iMethod.HasGenericParameters) {
				List<GenericParameter> m1p = new List<GenericParameter> (iMethod.GenericParameters);
				List<GenericParameter> m2p = new List<GenericParameter> (tMethod.GenericParameters);

				for (int i = 0; i < m1p.Count; i++)
					if (!CompareTypes (m1p [i], m2p [i]))
						return false;
			}

			return true;
		}

		bool FixAbstractMethods (TypeDefinition type)
		{
			if (!type.HasInterfaces)
				return false;

			bool rv = false;
			List<MethodDefinition> typeMethods = new List<MethodDefinition> (type.Methods);
			foreach (var baseType in type.GetBaseTypes (cache))
				typeMethods.AddRange (baseType.Methods);

			foreach (var ifaceInfo in type.Interfaces) {
				var iface    = ifaceInfo.InterfaceType;
				var ifaceDef = iface.Resolve ();
				if (ifaceDef == null) {
					LogMessage ($"Unable to unresolve interface: {iface.FullName}");
					continue;
				}
				if (ifaceDef.HasGenericParameters)
					continue;

				foreach (var iMethod in ifaceDef.Methods.Where (m => m.IsAbstract)) {
					bool exists = false;

					foreach (var tMethod in typeMethods) {
						if (HaveSameSignature (iface, iMethod, tMethod)) {
							exists = true;
							break;
						}
					}

					if (!exists) {
						AddNewExceptionMethod (type, iMethod);
						rv = true;
					}
				}
			}

			return rv;
		}

		TypeReference TryImportType (TypeDefinition declaringType, TypeReference type)
		{
			if (type.IsGenericParameter)
				return type;

			return declaringType.Module.Import (type);
		}

		void AddNewExceptionMethod (TypeDefinition type, MethodDefinition method)
		{
			var newMethod = new MethodDefinition (method.Name, (method.Attributes | MethodAttributes.Final) & ~MethodAttributes.Abstract, TryImportType (type, method.ReturnType));

			foreach (var paramater in method.Parameters)
				newMethod.Parameters.Add (new ParameterDefinition (paramater.Name, paramater.Attributes, TryImportType (type, paramater.ParameterType)));

			var ilP = newMethod.Body.GetILProcessor ();

			ilP.Append (ilP.Create (Mono.Cecil.Cil.OpCodes.Newobj, type.Module.Import (AbstractMethodErrorConstructor)));
			ilP.Append (ilP.Create (Mono.Cecil.Cil.OpCodes.Throw));

			type.Methods.Add (newMethod);

			LogMessage ($"Added method: {method} to type: {type.FullName} scope: {type.Scope}");
		}

		MethodReference abstractMethodErrorConstructor;

		MethodReference AbstractMethodErrorConstructor {
			get {
				if (abstractMethodErrorConstructor != null)
					return abstractMethodErrorConstructor;

				var assembly = GetMonoAndroidAssembly ();
				if (assembly != null) { 
					var errorException = assembly.MainModule.GetType ("Java.Lang.AbstractMethodError");
					if (errorException != null) {
						foreach (var method in errorException.Methods) {
							if (method.Name == ".ctor" && !method.HasParameters) {
								abstractMethodErrorConstructor = method;
								break;
							}
						}
					}
				}

				if (abstractMethodErrorConstructor == null)
					throw new Exception ("Unable to find Java.Lang.AbstractMethodError constructor in Mono.Android assembly");

				return abstractMethodErrorConstructor;
			}
		}

		public virtual void LogMessage (string message)
		{
			Context.LogMessage (message);
		}

		protected virtual AssemblyDefinition GetMonoAndroidAssembly ()
		{
#if !NET5_LINKER
			foreach (var assembly in Context.GetAssemblies ()) {
				if (assembly.Name.Name == "Mono.Android")
					return assembly;
			}
			return null;
#else
			return Context.GetLoadedAssembly ("Mono.Android");
#endif
		}
	}
}
