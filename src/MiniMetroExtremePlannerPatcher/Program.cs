using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace MiniMetroExtremePlannerPatcher
{
    internal static class Program
    {
        private const string TickSignature = "System.Void MiniMetroExtremePlanner.Bootstrap::Tick(Game,System.Single)";

        private static int Main(string[] args)
        {
            try
            {
                if ((args.Length == 4 || args.Length == 5)
                    && String.Equals(args[0], "patch", StringComparison.OrdinalIgnoreCase))
                {
                    Patch(
                        Path.GetFullPath(args[1]),
                        Path.GetFullPath(args[2]),
                        Path.GetFullPath(args[3]),
                        args.Length == 5 ? Path.GetFullPath(args[4]) : null);
                    return 0;
                }
                if (args.Length == 2 && String.Equals(args[0], "verify", StringComparison.OrdinalIgnoreCase))
                {
                    Verify(Path.GetFullPath(args[1]));
                    return 0;
                }
                if ((args.Length == 3 || args.Length == 4)
                    && String.Equals(args[0], "smoke", StringComparison.OrdinalIgnoreCase))
                {
                    CreateSmokeAssembly(
                        Path.GetFullPath(args[1]),
                        Path.GetFullPath(args[2]),
                        args.Length == 4 ? Path.GetFullPath(args[3]) : null);
                    return 0;
                }
                PrintUsage();
                return 2;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("ERROR: " + exception.Message);
                Console.Error.WriteLine(exception);
                return 1;
            }
        }

        private static void Patch(
            string inputPath,
            string outputPath,
            string pluginPath,
            string dependencyDirectory)
        {
            RequireFile(inputPath, "input Assembly-CSharp.dll");
            RequireFile(pluginPath, "planner plugin");
            if (String.Equals(inputPath, outputPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Input and output paths must differ.");
            }

            DefaultAssemblyResolver resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(Path.GetDirectoryName(inputPath));
            resolver.AddSearchDirectory(Path.GetDirectoryName(pluginPath));
            if (!String.IsNullOrEmpty(dependencyDirectory))
            {
                if (!Directory.Exists(dependencyDirectory))
                {
                    throw new DirectoryNotFoundException(
                        "Dependency directory not found: " + dependencyDirectory);
                }
                resolver.AddSearchDirectory(dependencyDirectory);
            }

            using (ModuleDefinition target = ModuleDefinition.ReadModule(
                inputPath,
                new ReaderParameters { AssemblyResolver = resolver }))
            {
                MethodDefinition update = RequireMethod(RequireType(target, "Game"), "Update", 1);
                if (ContainsTick(update))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
                    File.Copy(inputPath, outputPath, true);
                    Console.WriteLine("Planner hook already present; copied unchanged.");
                }
                else
                {
                    using (AssemblyDefinition plugin = AssemblyDefinition.ReadAssembly(pluginPath))
                    {
                        TypeDefinition bootstrap = RequireType(plugin.MainModule, "MiniMetroExtremePlanner.Bootstrap");
                        MethodDefinition tick = RequireMethod(bootstrap, "Tick", 2);
                        MethodReference tickReference = target.ImportReference(tick);
                        Instruction first = update.Body.Instructions[0];
                        ILProcessor il = update.Body.GetILProcessor();
                        il.InsertBefore(first, Instruction.Create(OpCodes.Ldarg_0));
                        il.InsertBefore(first, Instruction.Create(OpCodes.Ldarg_1));
                        il.InsertBefore(first, Instruction.Create(OpCodes.Call, tickReference));
                        update.Body.MaxStackSize = Math.Max(update.Body.MaxStackSize, 2);
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
                    target.Write(outputPath);
                    Console.WriteLine("Planner hook applied to Game.Update(float).");
                }
            }

            Verify(outputPath);
        }

        private static void Verify(string assemblyPath)
        {
            RequireFile(assemblyPath, "assembly to verify");
            using (ModuleDefinition module = ModuleDefinition.ReadModule(assemblyPath))
            {
                MethodDefinition update = RequireMethod(RequireType(module, "Game"), "Update", 1);
                if (!ContainsTick(update))
                {
                    throw new InvalidOperationException("Planner hook is missing from Game.Update(float).");
                }
                if (!module.AssemblyReferences.Any(reference => reference.Name == "MiniMetroExtremePlanner"))
                {
                    throw new InvalidOperationException("Planner assembly reference is missing.");
                }
            }
            Console.WriteLine("Planner patch verified.");
        }

        private static void CreateSmokeAssembly(
            string inputPath,
            string outputPath,
            string dependencyDirectory)
        {
            RequireFile(inputPath, "patched assembly for smoke test");
            if (String.Equals(inputPath, outputPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Smoke input and output paths must differ.");
            }
            Verify(inputPath);

            DefaultAssemblyResolver resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(Path.GetDirectoryName(inputPath));
            if (!String.IsNullOrEmpty(dependencyDirectory))
            {
                if (!Directory.Exists(dependencyDirectory))
                {
                    throw new DirectoryNotFoundException(
                        "Dependency directory not found: " + dependencyDirectory);
                }
                resolver.AddSearchDirectory(dependencyDirectory);
            }

            using (ModuleDefinition module = ModuleDefinition.ReadModule(
                inputPath,
                new ReaderParameters { AssemblyResolver = resolver }))
            {
                ReplaceBooleanGetter(module, "Main", "get_IsDemo", true);
                ReplaceBooleanGetter(module, "Game", "get_IsPaused", false);
                ReplaceBooleanGetter(module, "Game", "get_IsLocked", false);
                TypeDefinition game = RequireType(module, "Game");
                MethodDefinition update = RequireMethod(game, "Update", 1);
                MethodDefinition setScreen = RequireMethod(game, "set_Screen", 1);
                Instruction first = update.Body.Instructions[0];
                ILProcessor updateIl = update.Body.GetILProcessor();
                updateIl.InsertBefore(first, Instruction.Create(OpCodes.Ldarg_0));
                updateIl.InsertBefore(first, Instruction.Create(OpCodes.Ldc_I4_M1));
                updateIl.InsertBefore(first, Instruction.Create(OpCodes.Call, setScreen));
                update.Body.MaxStackSize = Math.Max(update.Body.MaxStackSize, 2);

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
                module.Write(outputPath);
            }

            Verify(outputPath);
            using (ModuleDefinition module = ModuleDefinition.ReadModule(outputPath))
            {
                RequireBooleanGetter(module, "Main", "get_IsDemo", true);
                RequireBooleanGetter(module, "Game", "get_IsPaused", false);
                RequireBooleanGetter(module, "Game", "get_IsLocked", false);
                MethodDefinition update = RequireMethod(RequireType(module, "Game"), "Update", 1);
                if (update.Body.Instructions.Count < 3
                    || update.Body.Instructions[0].OpCode != OpCodes.Ldarg_0
                    || update.Body.Instructions[1].OpCode != OpCodes.Ldc_I4_M1
                    || update.Body.Instructions[2].OpCode != OpCodes.Call
                    || !String.Equals(
                        ((MethodReference)update.Body.Instructions[2].Operand).FullName,
                        "System.Void Game::set_Screen(GameScreen)",
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Smoke build did not insert the native IntroScreen transition.");
                }
            }
            Console.WriteLine(
                "Smoke assembly created; built-in London demo start enabled and opening pause/lock/intro gates disabled.");
        }

        private static void ReplaceBooleanGetter(
            ModuleDefinition module,
            string typeName,
            string methodName,
            bool value)
        {
            MethodDefinition method = RequireMethod(RequireType(module, typeName), methodName, 0);
            method.Body.ExceptionHandlers.Clear();
            method.Body.Variables.Clear();
            method.Body.Instructions.Clear();
            ILProcessor il = method.Body.GetILProcessor();
            il.Append(Instruction.Create(value ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0));
            il.Append(Instruction.Create(OpCodes.Ret));
            method.Body.InitLocals = false;
            method.Body.MaxStackSize = 1;
        }

        private static void RequireBooleanGetter(
            ModuleDefinition module,
            string typeName,
            string methodName,
            bool value)
        {
            MethodDefinition method = RequireMethod(RequireType(module, typeName), methodName, 0);
            OpCode expected = value ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0;
            if (method.Body.Instructions.Count != 2
                || method.Body.Instructions[0].OpCode != expected
                || method.Body.Instructions[1].OpCode != OpCodes.Ret)
            {
                throw new InvalidOperationException(
                    "Smoke build boolean override is missing: " + typeName + "." + methodName);
            }
        }


        private static bool ContainsTick(MethodDefinition method)
        {
            return method.HasBody && method.Body.Instructions.Any(instruction =>
            {
                MethodReference reference = instruction.Operand as MethodReference;
                return reference != null && reference.FullName == TickSignature;
            });
        }

        private static TypeDefinition RequireType(ModuleDefinition module, string fullName)
        {
            TypeDefinition type = module.Types.FirstOrDefault(candidate => candidate.FullName == fullName);
            if (type == null) throw new InvalidOperationException("Required type not found: " + fullName);
            return type;
        }

        private static MethodDefinition RequireMethod(TypeDefinition type, string name, int parameterCount)
        {
            MethodDefinition method = type.Methods.FirstOrDefault(candidate =>
                candidate.Name == name && candidate.Parameters.Count == parameterCount);
            if (method == null || !method.HasBody)
            {
                throw new InvalidOperationException("Required method not found: " + type.FullName + "." + name);
            }
            return method;
        }

        private static void RequireFile(string path, string description)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Missing " + description, path);
        }

        private static void PrintUsage()
        {
            Console.Error.WriteLine("Usage:");
            Console.Error.WriteLine("  MiniMetroExtremePlannerPatcher patch <input Assembly-CSharp.dll> <output dll> <plugin dll> [dependency directory]");
            Console.Error.WriteLine("  MiniMetroExtremePlannerPatcher verify <patched Assembly-CSharp.dll>");
            Console.Error.WriteLine("  MiniMetroExtremePlannerPatcher smoke <patched Assembly-CSharp.dll> <output dll> [dependency directory]");
        }
    }
}
