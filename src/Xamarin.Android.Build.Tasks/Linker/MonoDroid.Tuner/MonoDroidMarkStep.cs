using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

using Java.Interop.Tools.Cecil;

using Mono.Linker;
using Mono.Linker.Steps;
using Mono.Tuner;

namespace MonoDroid.Tuner
{
	class MonoDroidMarkStep : MarkStep
	{
		const string ICustomMarshalerName = "System.Runtime.InteropServices.ICustomMarshaler";
		HashSet<TypeDefinition> marshalTypes = new HashSet<TypeDefinition> ();
		readonly TypeDefinitionCache cache;

		public MonoDroidMarkStep (TypeDefinitionCache cache)
		{
			this.cache = cache;
		}

		public override void Process (LinkContext context)
		{
			marshalTypes.Clear ();
			base.Process (context);

			if (PreserveJniMarshalMethods () && UpdateMarshalTypes ())
				base.Process (context);
		}

		bool UpdateMarshalTypes ()
		{
			MethodDefinition registerMethod;
			HashSet<string> markedMethods = new HashSet<string> ();
			var updated = false;

			var typeName = "Java.Interop.TypeManager/JavaTypeManager/__<$>_jni_marshal_methods";
			var javaTypeManager = _context.GetType ("Mono.Android", typeName);
			if (javaTypeManager == null)
				throw new InvalidOperationException ($"Unable to find '{typeName}' in Mono.Android.dll!");
			marshalTypes.Add (javaTypeManager);

			foreach (var type in marshalTypes) {
				registerMethod = null;
				markedMethods.Clear ();

				foreach (var method in type.Methods) {
					if (method.Name == "__RegisterNativeMembers") {
						registerMethod = method;

						continue;
					}

					if (method.IsConstructor)
						continue;

					if (_context.Annotations.IsMarked (method))
						markedMethods.Add (method.Name);
				}

				if (registerMethod == null || markedMethods.Count <= 0)
					continue;

				updated |= UpdateMarshalRegisterMethod (registerMethod, markedMethods);
			}

			UpdateMagicRegistration ();

			return updated;
		}

		MethodReference CreateGenericMethodReference (MethodReference method, GenericInstanceType type)
		{
			var genericMethod = new MethodReference (method.Name, method.ReturnType) {
				DeclaringType = type,
				HasThis = method.HasThis,
				ExplicitThis = method.ExplicitThis,
				CallingConvention = method.CallingConvention,
			};

			for (int i = 0; i < method.Parameters.Count; i++)
				genericMethod.Parameters.Add (method.Parameters [i]);

			return genericMethod;
		}

		void UpdateRegistrationSwitch (MethodDefinition method, MethodReference[] switchMethods)
		{
			var instructions = method.Body.Instructions;
			var module = method.DeclaringType.Module;
			var switchInstructions = new Instruction [switchMethods.Length];

			instructions.Clear ();

			var typeNullable = _context.GetType ("mscorlib", "System.Nullable`1");
			var methodGetValueOrDefault = Extensions.GetMethod (typeNullable, "GetValueOrDefault");
			var genericTypeNullable = new GenericInstanceType (typeNullable);
			genericTypeNullable.GenericArguments.Add (_context.GetType ("mscorlib", "System.Int32"));

			var typeIdxVariable = new VariableDefinition (module.ImportReference (genericTypeNullable));
			method.Body.Variables.Clear ();
			method.Body.Variables.Add (typeIdxVariable);

			instructions.Add (Instruction.Create (OpCodes.Ldarg_1));
			instructions.Add (Instruction.Create (OpCodes.Stloc_0));
			instructions.Add (Instruction.Create (OpCodes.Ldloca_S, typeIdxVariable));

			var genericMethodGetValueOrDefault = CreateGenericMethodReference (methodGetValueOrDefault, genericTypeNullable);
			instructions.Add (Instruction.Create (OpCodes.Call, module.ImportReference (genericMethodGetValueOrDefault)));

			for (var i = 0; i < switchMethods.Length; i++)
				switchInstructions [i] = Instruction.Create (OpCodes.Ldtoken, switchMethods [i].DeclaringType);

			var typeType = _context.GetType ("mscorlib", "System.Type");
			var methodGetTypeFromHandle = _context.GetMethod ("mscorlib", "System.Type", "GetTypeFromHandle", new string[] { "System.RuntimeTypeHandle" });
			var callDelegateStart = Instruction.Create (OpCodes.Call, module.ImportReference (methodGetTypeFromHandle));

			instructions.Add (Instruction.Create (OpCodes.Switch, switchInstructions));

			for (var i = 0; i < switchMethods.Length; i++) {
				instructions.Add (switchInstructions [i]);
				instructions.Add (Instruction.Create (OpCodes.Br, callDelegateStart));
			}

			instructions.Add (Instruction.Create (OpCodes.Ldc_I4_0));
			instructions.Add (Instruction.Create (OpCodes.Ret));

			var actionType = _context.GetType ("mscorlib", "System.Action`1");

			var genericActionType = new GenericInstanceType (actionType);
			var argsType = _context.GetType ("Java.Interop", "Java.Interop.JniNativeMethodRegistrationArguments");

			genericActionType.GenericArguments.Add (argsType);

			MarkType (genericActionType);

			var actionInvoke = Extensions.GetMethod (actionType, "Invoke", new string[] { "T" });
			var methodGetMethod = _context.GetMethod ("mscorlib", "System.Type", "GetMethod", new string[] { "System.String" });
			var typeMethodInfo = _context.GetType ("mscorlib", "System.Reflection.MethodInfo");
			var methodCreateDelegate = _context.GetMethod ("mscorlib", "System.Reflection.MethodInfo", "CreateDelegate", new string[] { "System.Type" });

			instructions.Add (callDelegateStart);

			instructions.Add (Instruction.Create (OpCodes.Ldstr, "__RegisterNativeMembers"));
			instructions.Add (Instruction.Create (OpCodes.Call, module.ImportReference (methodGetMethod)));

			instructions.Add (Instruction.Create (OpCodes.Ldtoken, module.ImportReference (genericActionType)));
			instructions.Add (Instruction.Create (OpCodes.Call, module.ImportReference (methodGetTypeFromHandle)));

			instructions.Add (Instruction.Create (OpCodes.Callvirt, module.ImportReference (methodCreateDelegate)));

			instructions.Add (Instruction.Create (OpCodes.Castclass, module.ImportReference (genericActionType)));

			var genericActionInvoke = CreateGenericMethodReference (actionInvoke, genericActionType);

			instructions.Add (Instruction.Create (OpCodes.Ldarg_0));
			instructions.Add (Instruction.Create (OpCodes.Callvirt, module.ImportReference (genericActionInvoke)));

			instructions.Add (Instruction.Create (OpCodes.Ldc_I4_1));
			instructions.Add (Instruction.Create (OpCodes.Ret));
		}

		void UpdateMagicPrefill (TypeDefinition magicType)
		{
			var fieldTypesMap = magicType.Fields.FirstOrDefault (f => f.Name == "typesMap");
			if (fieldTypesMap == null)
				return;

			var methodPrefill = Extensions.GetMethod (magicType, "Prefill");
			if (methodPrefill == null)
				return;

			var typeDictionary = _context.GetType ("mscorlib", "System.Collections.Generic.Dictionary`2");
			var ctorDictionary = Extensions.GetMethod (typeDictionary, ".ctor", new string[] { "System.Int32" });
			var methodSetItem = Extensions.GetMethod (typeDictionary, "set_Item", new string[] { "TKey", "TValue" });
			var genericTypeDictionary = new GenericInstanceType (typeDictionary);
			genericTypeDictionary.GenericArguments.Add (_context.GetType ("mscorlib", "System.String"));
			genericTypeDictionary.GenericArguments.Add (_context.GetType ("mscorlib", "System.Int32"));

			var genericMethodDictionaryCtor = CreateGenericMethodReference (ctorDictionary, genericTypeDictionary);
			var genericMethodDictionarySetItem = CreateGenericMethodReference (methodSetItem, genericTypeDictionary);
			var importedMethodSetItem = magicType.Module.ImportReference (genericMethodDictionarySetItem);

			var instructions = methodPrefill.Body.Instructions;
			instructions.Clear ();

			instructions.Add (Extensions.CreateLoadArraySizeOrOffsetInstruction (marshalTypes.Count));
			instructions.Add (Instruction.Create (OpCodes.Newobj, magicType.Module.ImportReference (genericMethodDictionaryCtor)));
			instructions.Add (Instruction.Create (OpCodes.Stsfld, fieldTypesMap));

			int idx = 0;

			foreach (var type in marshalTypes) {
				instructions.Add (Instruction.Create (OpCodes.Ldsfld, fieldTypesMap));
				instructions.Add (Instruction.Create (OpCodes.Ldstr, type.FullName.Replace ("/__<$>_jni_marshal_methods", "").Replace ("/","+")));
				instructions.Add (Extensions.CreateLoadArraySizeOrOffsetInstruction (idx++));
				instructions.Add (Instruction.Create (OpCodes.Callvirt, importedMethodSetItem));
			}

			instructions.Add (Instruction.Create (OpCodes.Ret));
		}

		void UpdateMagicRegistration ()
		{
			TypeDefinition magicType = _context.GetType ("Mono.Android", "Android.Runtime.AndroidTypeManager/MagicRegistrationMap");
			if (magicType == null)
				return;

			MethodDefinition magicCall = Extensions.GetMethod (magicType, "CallRegisterMethodByIndex");
			if (magicCall == null)
				return;

			var switchMethods = new MethodReference [marshalTypes.Count];
			var module = magicType.Module;
			int idx = 0;
			foreach (var type in marshalTypes) {
				var md = Extensions.GetMethod (type, "__RegisterNativeMembers");
				if (md == null)
					return;

				var resolved = md.Resolve ();
				if (resolved == null)
					return;

				switchMethods [idx++] = module.ImportReference (resolved);
			}

			UpdateMagicPrefill (magicType);
			UpdateRegistrationSwitch (magicCall, switchMethods);
		}

		static bool IsLdcI4 (Instruction instruction, out int intValue)
		{
			intValue = 0;

			if (instruction.OpCode == OpCodes.Ldc_I4_0)
				intValue = 0;
			else if (instruction.OpCode == OpCodes.Ldc_I4_1)
				intValue = 1;
			else if (instruction.OpCode == OpCodes.Ldc_I4_2)
				intValue = 2;
			else if (instruction.OpCode == OpCodes.Ldc_I4_3)
				intValue = 3;
			else if (instruction.OpCode == OpCodes.Ldc_I4_4)
				intValue = 4;
			else if (instruction.OpCode == OpCodes.Ldc_I4_5)
				intValue = 5;
			else if (instruction.OpCode == OpCodes.Ldc_I4_6)
				intValue = 6;
			else if (instruction.OpCode == OpCodes.Ldc_I4_7)
				intValue = 7;
			else if (instruction.OpCode == OpCodes.Ldc_I4_8)
				intValue = 8;
			else if (instruction.OpCode == OpCodes.Ldc_I4)
				intValue = (int) instruction.Operand;
			else if (instruction.OpCode == OpCodes.Ldc_I4_S)
				intValue = (sbyte) instruction.Operand;
			else
				return false;

			return true;
		}

		bool UpdateMarshalRegisterMethod (MethodDefinition method, HashSet<string> markedMethods)
		{
			var instructions = method.Body.Instructions;
			var arraySizeUpdated = false;
			var idx = 0;
			var arrayOffset = 0;

			while (idx < instructions.Count) {
				if (!arraySizeUpdated && idx + 1 < instructions.Count) {
					int length;
					if (IsLdcI4 (instructions [idx++], out length) && instructions [idx].OpCode == OpCodes.Newarr) {
						instructions [idx - 1] = Extensions.CreateLoadArraySizeOrOffsetInstruction (markedMethods.Count);
						idx++;
						arraySizeUpdated = true;
						continue;
					}
				} else if (idx + 9 < instructions.Count) {
					var chunkStart = idx;
					if (instructions [idx++].OpCode != OpCodes.Dup)
						continue;

					int offset;
					var offsetIdx = idx;
					if (!IsLdcI4 (instructions [idx++], out offset))
						continue;

					if (instructions [idx++].OpCode != OpCodes.Ldstr)
						continue;

					if (instructions [idx++].OpCode != OpCodes.Ldstr)
						continue;

					if (instructions [idx++].OpCode != OpCodes.Ldnull)
						continue;

					if (instructions [idx++].OpCode != OpCodes.Ldftn)
						continue;

					if (!(instructions [idx - 1].Operand is MethodReference mr))
						continue;

					if (instructions [idx++].OpCode != OpCodes.Newobj)
						continue;

					if (instructions [idx++].OpCode != OpCodes.Newobj)
						continue;

					var chunkEnd = idx;
					if (instructions [idx++].OpCode != OpCodes.Stelem_Any)
						continue;

					if (markedMethods.Contains (mr.Name)) {
						instructions [offsetIdx] = Extensions.CreateLoadArraySizeOrOffsetInstruction (arrayOffset++);
						continue;
					}

					for (int i = 0; i <= chunkEnd - chunkStart; i++)
						instructions.RemoveAt (chunkStart);

					idx = chunkStart;
				} else
					break;
			}

			if (!arraySizeUpdated || arrayOffset != markedMethods.Count) {
				_context.LogMessage ($"Unable to update {method} size updated {arraySizeUpdated} counts {arrayOffset} {markedMethods.Count}");
				return false;
			}

			MarkMethod (method);

			return true;
		}

		bool PreserveJniMarshalMethods ()
		{
			if (_context is AndroidLinkContext ac)
				return ac.PreserveJniMarshalMethods;

			return false;
		}

		// If this is one of our infrastructure methods that has [Register], like:
		// [Register ("hasWindowFocus", "()Z", "GetHasWindowFocusHandler")],
		// we need to preserve the "GetHasWindowFocusHandler" method as well.
		protected override void DoAdditionalMethodProcessing (MethodDefinition method)
		{
			string member, nativeMethod, signature;

			bool preserveJniMarshalMethodOnly = false;
			if (!method.TryGetRegisterMember (out member, out nativeMethod, out signature)) {
				if (PreserveJniMarshalMethods () &&
				    method.DeclaringType.GetMarshalMethodsType () != null &&
				    method.TryGetBaseOrInterfaceRegisterMember (cache, out member, out nativeMethod, out signature)) {
					preserveJniMarshalMethodOnly = true;
				} else {
					return;
				}
			}

			MethodDefinition marshalMethod;
			if (PreserveJniMarshalMethods () && method.TryGetMarshalMethod (nativeMethod, signature, out marshalMethod)) {
				MarkMethod (marshalMethod);
				marshalTypes.Add (marshalMethod.DeclaringType);
			}

			if (preserveJniMarshalMethodOnly)
				return;

			PreserveRegisteredMethod (method.DeclaringType, member);
		}

		protected override bool ShouldMarkInterfaceImplementation (TypeDefinition type, InterfaceImplementation iface, TypeDefinition resolvedInterfaceType)
		{
			if (Annotations.IsMarked (iface))
				return false;

			if (base.ShouldMarkInterfaceImplementation (type, iface, resolvedInterfaceType))
				return true;

			return resolvedInterfaceType.ImplementsIJavaObject () || resolvedInterfaceType.ImplementsIJavaPeerable ();
		}

		protected override void DoAdditionalTypeProcessing (TypeDefinition type)
		{
			// If we are preserving a Mono.Android interface,
			// preserve all members on the interface.
			if (!type.IsInterface)
				return;

			// Mono.Android interfaces will always inherit IJavaObject
			if (!type.ImplementsIJavaObject ())
				return;

			foreach (MethodReference method in type.Methods)
				MarkMethod (method);
		}

		private void PreserveRegisteredMethod (TypeDefinition type, string member)
		{
			var type_ptr = type;
			var pos = member.IndexOf (':');

			if (pos > 0) {
				var type_name = member.Substring (pos + 1);
				member = member.Substring (0, pos);
				type_ptr = type_ptr.Module.Types.FirstOrDefault (t => t.FullName == type_name);
			}

			if (type_ptr == null)
				return;

			while (MarkNamedMethod (type, member) == 0 && type.BaseType != null)
				type = type.BaseType.Resolve ();
		}

		protected override TypeDefinition MarkType (TypeReference reference)
		{
			TypeDefinition type = base.MarkType (reference);
			if (type == null)
				return null;

			switch (type.Module.Assembly.Name.Name) {
			case "mscorlib":
				ProcessCorlib (type);
				break;
			case "System.Core":
				ProcessSystemCore (type);
				break;
			case "System.Data":
				ProcessSystemData (type);
				break;
			case "System":
				ProcessSystem (type);
				break;
			}

			if (type.HasMethods && type.HasInterfaces && type.Implements (ICustomMarshalerName)) {
				foreach (MethodDefinition method in type.Methods) {
					if (method.Name == "GetInstance" && method.IsStatic && method.HasParameters && method.Parameters.Count == 1 && method.ReturnType.FullName == ICustomMarshalerName && method.Parameters.First ().ParameterType.FullName == "System.String") {
						MarkMethod (method);
						break;
					}
				}
			}

			return type;
		}

		bool DebugBuild {
			get { return _context.LinkSymbols; }
		}

		void ProcessCorlib (TypeDefinition type)
		{
			switch (type.Namespace) {
			case "System.Runtime.CompilerServices":
				switch (type.Name) {
				case "AsyncTaskMethodBuilder":
					if (DebugBuild) {
						MarkNamedMethod (type, "SetNotificationForWaitCompletion");
						MarkNamedMethod (type, "get_ObjectIdForDebugger");
					}
					break;
				case "AsyncTaskMethodBuilder`1":
					if (DebugBuild) {
						MarkNamedMethod (type, "SetNotificationForWaitCompletion");
						MarkNamedMethod (type, "get_ObjectIdForDebugger");
					}
					break;
				}
				break;
			case "System.Threading.Tasks":
				switch (type.Name) {
				case "Task":
					if (DebugBuild)
						MarkNamedMethod (type, "NotifyDebuggerOfWaitCompletion");
					break;
				}
				break;
			}
		}

		void ProcessSystemCore (TypeDefinition type)
		{
			switch (type.Namespace) {
			case "System.Linq.Expressions":
				switch (type.Name) {
				case "LambdaExpression":
					var expr_t = type.Module.GetType ("System.Linq.Expressions.Expression`1");
					if (expr_t != null)
						MarkNamedMethod (expr_t, "Create");
					break;
				}
				break;
			case "System.Linq.Expressions.Compiler":
				switch (type.Name) {
				case "LambdaCompiler":
					MarkNamedMethod (type.Module.GetType ("System.Runtime.CompilerServices.RuntimeOps"), "Quote");
					break;
				}
				break;
			}
		}

		void ProcessSystemData (TypeDefinition type)
		{
			switch (type.Namespace) {
			case "System.Data.SqlTypes":
				switch (type.Name) {
				case "SqlXml":
					// TODO: Needed only if CreateSqlReaderDelegate is used
					TypeDefinition xml_reader = _context.GetType ("System.Xml", "System.Xml.XmlReader");
					MarkNamedMethod (xml_reader, "CreateSqlReader");
					break;
				}
				break;
			}
		}

		void ProcessSystem (TypeDefinition type)
		{
			switch (type.Namespace) {
			case "System.Diagnostics":
				switch (type.Name) {
				// see mono/metadata/process.c
				case "FileVersionInfo":
				case "ProcessModule":
					// fields are initialized by the runtime, if the type is here then all (instance) fields must be present
					MarkFields (type, false);
					break;
				}
				break;
			case "System.Net.Sockets":
				switch (type.Name) {
				case "IPAddress":
					// mono/metadata/socket-io.c directly access 'm_Address' and 'm_Numbers'
					MarkFields (type, false);
					break;
				case "IPv6MulticastOption":
					// mono/metadata/socket-io.c directly access 'group' and 'ifIndex' private instance fields
					MarkFields (type, false);
					break;
				case "LingerOption":
					// mono/metadata/socket-io.c directly access 'enabled' and 'seconds' private instance fields
					MarkFields (type, false);
					break;
				case "MulticastOption":
					// mono/metadata/socket-io.c directly access 'group' and 'local' private instance fields
					MarkFields (type, false);
					break;
				case "Socket":
					// mono/metadata/socket-io.c directly access 'ipv4Supported', 'ipv6Supported' (static) and 'socket' (instance)
					MarkFields (type, true);
					break;
				case "SocketAddress":
					// mono/metadata/socket-io.c directly access 'data'
					MarkFields (type, false);
					break;
				}
				break;
			case "":
				if (!type.IsNested)
					break;

				switch (type.Name) {
				case "SocketAsyncResult":
					// mono/metadata/socket-io.h defines this structure (MonoSocketAsyncResult) for the runtime usage
					MarkFields (type, false);
					break;
				case "ProcessAsyncReader":
					// mono/metadata/socket-io.h defines this structure (MonoSocketAsyncResult) for the runtime usage
					MarkFields (type, false);
					break;
				}
				break;
			}
		}
	}
}
