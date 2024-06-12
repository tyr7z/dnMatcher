using dnlib.DotNet;
using dnlib.DotNet.Writer;
using Mono.Cecil;
using TypeDefinition = Mono.Cecil.TypeDefinition;
using ICustomAttributeProvider = Mono.Cecil.ICustomAttributeProvider;
using Mono.Collections.Generic;

/* TODO:
 * - Tidy up mapping appliances.
 * - Add support of obfuscation patterns to improve precision.
 * - Add more weighting to improve accuracy.
 */

namespace dnMatcher
{
    public class MethodDetails
    {
        public string TypeName { get; set; }
        public string MethodName { get; set; }
        public string ReturnType { get; set; }
        public List<string> ParameterTypes { get; set; }
        public bool IsPublic { get; set; }
        public bool IsPrivate { get; set; }
        public bool HasOverrides { get; set; }
        public Collection<ParameterDefinition> Parameters { get; set; }

        public override bool Equals(object obj)
        {
            if (obj is MethodDetails other)
            {
                return TypeName == other.TypeName &&
                       ReturnType == other.ReturnType &&
                       ParameterTypes.SequenceEqual(other.ParameterTypes) &&
                       IsPublic == other.IsPublic &&
                       IsPrivate == other.IsPrivate &&
                       HasOverrides == other.HasOverrides;
            }
            return false;
        }
    }

    public class ParameterDetails
    {
        public string ParameterName { get; set; }
        public string ParameterType { get; set; }
        public bool IsOut { get; set; }
        public bool IsOptional { get; set; }
        public bool HasConstant { get; set; }
        public bool HasDefault { get; set; }

        public override bool Equals(object obj)
        {
            if (obj == null || GetType() != obj.GetType())
                return false;

            var other = (ParameterDetails)obj;

            return ParameterType == other.ParameterType &&
                   IsOut == other.IsOut &&
                   IsOptional == other.IsOptional &&
                   HasConstant == other.HasConstant &&
                   HasDefault == other.HasDefault;
        }
    }

    public class FieldDetails
    {
        public string TypeName { get; set; }
        public string FieldName { get; set; }
        public string FieldType { get; set; }
        public int Offset { get; set; }
        public bool IsPrivate { get; set; }
        public bool IsPublic { get; set; }
        public bool IsStatic { get; set; }
        public bool HasConstant { get; set; }
        public bool HasDefault { get; set; }
        public bool IsLiteral { get; set; }

        public override bool Equals(object obj)
        {
            if (obj == null || GetType() != obj.GetType())
            {
                return false;
            }

            FieldDetails other = (FieldDetails)obj;

            return TypeName == other.TypeName &&
                   FieldType == other.FieldType &&
                   Offset == other.Offset &&
                   IsPrivate == other.IsPrivate &&
                   IsPublic == other.IsPublic &&
                   IsStatic == other.IsStatic &&
                   HasConstant == other.HasConstant &&
                   HasDefault == other.HasDefault &&
                   IsLiteral == other.IsLiteral;
        }
    }
    
    class Program
    {
        private static Dictionary<string, string> typeMapping = new();
        private static List<TypeDefinition>? newAssemblyTypes;

        static bool debugEnabled = false;
        static bool ignoreErrors = false;
        static bool minifiedMapping = false;

        static void Main(string[] args)
        {
            if (args.Length == 0 || args[0] == "--help")
            {
                PrintHelp();
                return;
            }

            string unobfDllPath = string.Empty;
            string dllPath = string.Empty;
            string mappingFilePath = string.Empty;
            string tmpPath1 = Guid.NewGuid().ToString() + ".dll";
            string outputPath = string.Empty;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-u" || args[i] == "--unobf-dll")
                {
                    if (i + 1 < args.Length)
                        unobfDllPath = args[i + 1];
                }
                else if (args[i] == "-d" || args[i] == "--dll")
                {
                    if (i + 1 < args.Length)
                        dllPath = args[i + 1];
                }
                else if (args[i] == "-m" || args[i] == "--mapping")
                {
                    if (i + 1 < args.Length)
                        mappingFilePath = args[i + 1];
                }
                else if (args[i] == "-o" || args[i] == "--output")
                {
                    if (i + 1 < args.Length)
                        outputPath = args[i + 1];
                }
                else if (args[i] == "--debug")
                {
                    debugEnabled = true;
                }
                else if (args[i] == "--ignore-errors")
                {
                    ignoreErrors = true;
                }
                else if (args[i] == "--minified-mapping")
                {
                    minifiedMapping = true;
                }
            }

            if (string.IsNullOrEmpty(unobfDllPath) || string.IsNullOrEmpty(dllPath) || string.IsNullOrEmpty(mappingFilePath) || string.IsNullOrEmpty(outputPath))
            {
                Print("Please provide the required arguments.\n", ConsoleColor.Red);
                PrintHelp();
                return;
            }

            if (!File.Exists(unobfDllPath))
            {
                Print("DLL file not found: ", ConsoleColor.Red); Print(unobfDllPath + "\n", ConsoleColor.DarkGray);
                return;
            }

            if (!File.Exists(dllPath))
            {
                Print("DLL file not found: ", ConsoleColor.Red); Print(dllPath + "\n", ConsoleColor.DarkGray);
                return;
            }

            var oldAssembly = ModuleDefinition.ReadModule(unobfDllPath);
            Console.WriteLine("[INFO] Loading old assembly...");
            var newAssembly = ModuleDefinition.ReadModule(dllPath);
            Console.WriteLine("[INFO] Loading new assembly...");
            Console.WriteLine($"[TYPES] Old assembly has {oldAssembly.Types.Count(t => t.Namespace == string.Empty)} types in root namespace");
            Console.WriteLine($"[TYPES] New assembly has {newAssembly.Types.Count(t => t.Namespace == string.Empty)} types in root namespace");
            Console.WriteLine($"[TYPES] Performing type matching...");
            newAssemblyTypes = newAssembly.Types.Where(t => t.Namespace == string.Empty).ToList();
            foreach (var originalType in oldAssembly.Types.Where(t => t.Namespace == string.Empty).OrderBy(t => t.BaseType?.FullName != "System.Object"))
            {
                var obfuscatedType = FindNewType(originalType);
                if (obfuscatedType == null)
                {
                    Console.WriteLine("[TYPES] Could not find suitable match for " + originalType);
                    continue;
                }
                if (!typeMapping.ContainsKey(obfuscatedType.FullName))
                {
                    typeMapping.Add(obfuscatedType.FullName, originalType.FullName);
                }
            }
            Console.WriteLine($"[TYPES] Matched {typeMapping.Count} types between both assemblies.");

            if (typeMapping.Count == 0)
            {
                Print("Mapping file is empty or invalid.", ConsoleColor.Red);
                return;
            }

            if (ApplyMapping(dllPath, tmpPath1, typeMapping) == 1)
                return;

            //var firstType = oldAssembly.Types.First(t => t.FullName == typeMapping.First().Key);
            var deobfAssembly = ModuleDefinition.ReadModule(tmpPath1);
            Console.WriteLine("[INFO] Loading deobfuscated assembly...");
            var methodList1 = new List<MethodDetails>();
            var methodList2 = new List<MethodDetails>();
            for (int i = 0; i < typeMapping.Count; i++)
            {
                string type = typeMapping.ElementAt(i).Value;
                foreach (var oldMethod in oldAssembly.GetType(string.Empty, type).Methods)
                {
                    var name = oldMethod.Name;
                    var returnType = oldMethod.ReturnType.FullName;
                    var parameterTypes = oldMethod.Parameters.Select(p => p.ParameterType.FullName).ToList();
                    var isPublic = oldMethod.IsPublic;
                    var isPrivate = oldMethod.IsPrivate;
                    var hasOverrides = oldMethod.HasOverrides;
                    var parameters = oldMethod.Parameters;
                    var methodDetails = new MethodDetails
                    {
                        TypeName = type,
                        MethodName = name,
                        ReturnType = returnType,
                        ParameterTypes = parameterTypes,
                        IsPublic = isPublic,
                        IsPrivate = isPrivate,
                        HasOverrides = hasOverrides,
                        Parameters = parameters
                    };
                    methodList1.Add(methodDetails);
                }
                foreach (var newMethod in deobfAssembly.GetType(string.Empty, type).Methods)
                {
                    var name = newMethod.Name;
                    var returnType = newMethod.ReturnType.FullName;
                    var parameterTypes = newMethod.Parameters.Select(p => p.ParameterType.FullName).ToList();
                    var isPublic = newMethod.IsPublic;
                    var isPrivate = newMethod.IsPrivate;
                    var hasOverrides = newMethod.HasOverrides;
                    var parameters = newMethod.Parameters;
                    var methodDetails = new MethodDetails
                    {
                        TypeName = type,
                        MethodName = name,
                        ReturnType = returnType,
                        ParameterTypes = parameterTypes,
                        IsPublic = isPublic,
                        IsPrivate = isPrivate,
                        HasOverrides = hasOverrides,
                        Parameters = parameters
                    };
                    methodList2.Add(methodDetails);
                }
            }

            var methodMatches = FindMethodMatches(methodList1.Distinct().ToList(), methodList2.Distinct().ToList());
            Dictionary<string, string> methodMapping = new();
            foreach (var match in methodMatches)
            {
                if (!methodMapping.ContainsKey(match.Item1.MethodName))
                {
                    methodMapping.Add(match.Item1.MethodName, match.Item2.MethodName);
                }
            }

            var fieldList1 = new List<FieldDetails>();
            var fieldList2 = new List<FieldDetails>();
            for (int i = 0; i < typeMapping.Count; i++)
            {
                string type = typeMapping.ElementAt(i).Value;
                foreach (var oldField in oldAssembly.GetType(string.Empty, type).Fields)
                {
                    var name = oldField.Name;
                    var fieldType = oldField.FieldType.FullName;
                    var isPrivate = oldField.IsPrivate;
                    var isPublic = oldField.IsPublic;
                    var isStatic = oldField.IsStatic;
                    var hasConstant = oldField.HasConstant;
                    var hasDefault = oldField.HasDefault;
                    var isLiteral = oldField.IsLiteral;
                    var fieldDetails = new FieldDetails
                    {
                        TypeName = type,
                        FieldName = name,
                        FieldType = fieldType,
                        IsPrivate = isPrivate,
                        IsPublic = isPublic,
                        IsStatic = isStatic,
                        HasConstant = hasConstant,
                        HasDefault = hasDefault,
                        IsLiteral = isLiteral
                    };
                    fieldList1.Add(fieldDetails);
                }
                foreach (var newField in deobfAssembly.GetType(string.Empty, type).Fields)
                {
                    var name = newField.Name;
                    var fieldType = newField.FieldType.FullName;
                    var isPrivate = newField.IsPrivate;
                    var isPublic = newField.IsPublic;
                    var isStatic = newField.IsStatic;
                    var hasConstant = newField.HasConstant;
                    var hasDefault = newField.HasDefault;
                    var isLiteral = newField.IsLiteral;
                    var fieldDetails = new FieldDetails
                    {
                        TypeName = type,
                        FieldName = name,
                        FieldType = fieldType,
                        IsPrivate = isPrivate,
                        IsPublic = isPublic,
                        IsStatic = isStatic,
                        HasConstant = hasConstant,
                        HasDefault = hasDefault,
                        IsLiteral = isLiteral
                    };
                    fieldList2.Add(fieldDetails);
                }
            }

            var fieldMatches = FindFieldMatches(fieldList1.Distinct().ToList(), fieldList2.Distinct().ToList());
            Dictionary<string, string> fieldMapping = new();
            foreach (var match in fieldMatches)
            {
                if (!fieldMapping.ContainsKey(match.Item1.FieldName))
                {
                    //Console.WriteLine($"{match.Item1.Name} -> {match.Item2.Name}");
                    fieldMapping.Add(match.Item1.FieldName, match.Item2.FieldName);
                }
            }

            deobfAssembly.Dispose();

            var mapping = new[] { typeMapping, methodMapping, fieldMapping }
            .SelectMany(dict => dict)
            .GroupBy(pair => pair.Key)
            .ToDictionary(group => group.Key, group => group.First().Value);

            string tmpPath2 = Guid.NewGuid().ToString() + ".dll";

            if (ApplyMapping(tmpPath1, tmpPath2, mapping) == 1)
                return;

            File.Delete(tmpPath1);

            var parameterList1 = new List<ParameterDetails>();
            var parameterList2 = new List<ParameterDetails>();
            for (int i = 0; i < methodMatches.Count; i++)
            {
                foreach (var parameter in methodMatches.ElementAt(i).Item1.Parameters)
                {
                    var name = parameter.Name;
                    var parameterType = parameter.ParameterType;
                    var attributes = parameter.Attributes;
                    var isOut = parameter.IsOut;
                    var isOptional = parameter.IsOptional;
                    var hasConstant = parameter.HasConstant;
                    var hasDefault = parameter.HasDefault;
                    var parameterDetails = new ParameterDetails
                    {
                        ParameterName = name,
                        ParameterType = parameterType.Name,
                        IsOut = isOut,
                        IsOptional = isOptional,
                        HasConstant = hasConstant,
                        HasDefault = hasDefault
                    };
                    parameterList1.Add(parameterDetails);
                }
                foreach (var parameter in methodMatches.ElementAt(i).Item2.Parameters)
                {
                    var name = parameter.Name;
                    var parameterType = parameter.ParameterType;
                    var attributes = parameter.Attributes;
                    var isOut = parameter.IsOut;
                    var isOptional = parameter.IsOptional;
                    var hasConstant = parameter.HasConstant;
                    var hasDefault = parameter.HasDefault;
                    var parameterDetails = new ParameterDetails
                    {
                        ParameterName = name,
                        ParameterType = parameterType.Name,
                        IsOut = isOut,
                        IsOptional = isOptional,
                        HasConstant = hasConstant,
                        HasDefault = hasDefault
                    };
                    parameterList2.Add(parameterDetails);
                }
            }

            Dictionary<string, string> parameterMapping = new();
            if (parameterList2.Count == parameterList1.Count)
            {
                for (int i = 0; i < parameterList2.Count; i++)
                {
                    if (parameterList2[i].Equals(parameterList1[i]))
                    {
                        if (!parameterMapping.ContainsKey(parameterList1[i].ParameterName))
                        {
                            //Console.WriteLine($"{parameterList1[i].Name} -> {parameterList2[i].Name}");
                            parameterMapping.Add(parameterList1[i].ParameterName, parameterList2[i].ParameterName);
                        }
                    }
                }
            }

            if (ApplyMapping(tmpPath2, outputPath, parameterMapping) == 1)
                return;

            File.Delete(tmpPath2);

            string filePath = Path.Combine(Directory.GetCurrentDirectory(), mappingFilePath);
            using (StreamWriter writer = new StreamWriter(filePath))
            {
                foreach (KeyValuePair<string, string> kvp in typeMapping)
                {
                    if (minifiedMapping && kvp.Key == kvp.Value) continue;
                    writer.WriteLine($"{kvp.Key} -> {kvp.Value}");
                }
                foreach (KeyValuePair<string, string> kvp in methodMapping)
                {
                    if (minifiedMapping && kvp.Key == kvp.Value) continue;
                    writer.WriteLine($"{kvp.Key} -> {kvp.Value}");
                }
                foreach (KeyValuePair<string, string> kvp in fieldMapping)
                {
                    if (minifiedMapping && kvp.Key == kvp.Value) continue;
                    writer.WriteLine($"{kvp.Key} -> {kvp.Value}");
                }
                foreach (KeyValuePair<string, string> kvp in parameterMapping)
                {
                    if (minifiedMapping && kvp.Key == kvp.Value) continue;
                    writer.WriteLine($"{kvp.Key} -> {kvp.Value}");
                }
            }
            Console.WriteLine($"Mapping dictionaries written to file: {filePath}");
        }

        private static int ApplyMapping(string dllPath, string outputPath, Dictionary<string, string> mapping)
        {
            try
            {
                using ModuleDefMD module = ModuleDefMD.Load(dllPath);
                foreach (var type in module.Types)
                {
                    string? deobfuscatedTypeName = GetDeobfuscatedValue(mapping, type.Name);
                    if (!string.IsNullOrEmpty(deobfuscatedTypeName))
                    {
                        Debug($"Type: {type.Name} -> {deobfuscatedTypeName}");
                        type.Name = deobfuscatedTypeName;
                    }

                    foreach (var method in type.Methods)
                    {
                        string? deobfuscatedMethodName = GetDeobfuscatedValue(mapping, method.Name);
                        if (!string.IsNullOrEmpty(deobfuscatedMethodName))
                        {
                            Debug($"Method: {method.Name} -> {deobfuscatedMethodName}");
                            method.Name = deobfuscatedMethodName;
                            SetCilBodyKeepOldMaxStack(method);
                        }

                        foreach (var parameter in method.Parameters)
                        {
                            string? deobfuscatedParameterName = GetDeobfuscatedValue(mapping, parameter.Name);
                            if (!string.IsNullOrEmpty(deobfuscatedParameterName))
                            {
                                Debug($"Parameter: {parameter.Name} -> {deobfuscatedParameterName}");
                                parameter.Name = deobfuscatedParameterName;
                            }
                        }
                    }

                    foreach (var field in type.Fields)
                    {
                        string? deobfuscatedFieldName = GetDeobfuscatedValue(mapping, field.Name);
                        if (!string.IsNullOrEmpty(deobfuscatedFieldName))
                        {
                            Debug($"Field: {field.Name} -> {deobfuscatedFieldName}");
                            field.Name = deobfuscatedFieldName;
                        }
                    }

                    foreach (var property in type.Properties)
                    {
                        string? deobfuscatedPropertyName = GetDeobfuscatedValue(mapping, property.Name);
                        if (!string.IsNullOrEmpty(deobfuscatedPropertyName))
                        {
                            Debug($"Property: {property.Name} -> {deobfuscatedPropertyName}");
                            property.Name = deobfuscatedPropertyName;
                        }
                    }
                }

                ModuleWriterOptions writerOptions = new(module);
                if (ignoreErrors)
                {
                    writerOptions.MetadataOptions.Flags |= MetadataFlags.KeepOldMaxStack;
                }
                try
                {
                    using (FileStream stream = File.Create(outputPath))
                    {
                        module.Write(stream, writerOptions);
                        module.Dispose();
                        stream.Close();
                    }
                    Print("Deobfuscation completed successfully!\n", ConsoleColor.Green);
                    return 0;
                }
                catch (Exception ex)
                {
                    Print("Error occurred during writing the output DLL file: ", ConsoleColor.Red); Print(ex.Message + "\n", ConsoleColor.DarkGray);
                    Print("\nTry using the ", ConsoleColor.Yellow); Print("--ignore-errors", ConsoleColor.DarkYellow); Print(" argument!\n", ConsoleColor.Yellow);
                    return 1;
                }
            }
            catch (Exception ex)
            {
                if (ignoreErrors)
                {
                    Print("An error occurred during deobfuscation, but it was ignored: ", ConsoleColor.Yellow); Print(ex.Message + "\n", ConsoleColor.DarkGray);
                    Console.WriteLine("Deobfuscation completed with errors!");
                    return 1;
                }
                else
                {
                    Print("Error occurred during deobfuscation: ", ConsoleColor.Red); Print(ex.Message + "\n", ConsoleColor.DarkGray);
                    Print("\nTry using the ", ConsoleColor.Yellow); Print("--ignore-errors", ConsoleColor.DarkYellow); Print(" argument!\n", ConsoleColor.Yellow);
                    return 1;
                }
            }
        }

        private static List<(FieldDetails, FieldDetails)> FindFieldMatches(List<FieldDetails> list1, List<FieldDetails> list2)
        {
            var matches = new List<(FieldDetails, FieldDetails)>();

            var buffer1 = list1.ToList();
            foreach (var item2 in list2.ToList())
            {
                var matchingEntries = buffer1.Where(item1 => item1.Equals(item2)).ToList();

                if (matchingEntries.Count >= 1)
                {
                    buffer1.Remove(matchingEntries[0]);
                    list2.Remove(item2);
                    matches.Add((item2, matchingEntries[0]));
                }
            }

            return matches;
        }

        private static List<(MethodDetails, MethodDetails)> FindMethodMatches(List<MethodDetails> list1, List<MethodDetails> list2)
        {
            var matches = new List<(MethodDetails, MethodDetails)>();

            foreach (var item2 in list2)
            {
                var matchingEntries = list1.Where(item1 => item1.Equals(item2)).ToList();

                if (matchingEntries.Count >= 1)
                {
                    matches.Add((item2, matchingEntries[0]));
                }
            }

            return matches;
        }

        private static TypeDefinition? FindNewType(TypeDefinition oldType)
        {
            var bestSimilarity = 0.0;
            const double lowestAllowedSimilarity = 1.15;
            TypeDefinition? bestMatch = null;

            if (newAssemblyTypes == null) return bestMatch;
            foreach (var newType in newAssemblyTypes)
            {
                if (oldType.FullName == newType.FullName) return newType;
                if (typeMapping.ContainsKey(newType.FullName)) continue;
                var similarity = CalculateSimilarity(oldType, newType);
                if (!(similarity > bestSimilarity) || similarity < lowestAllowedSimilarity) continue;
                bestSimilarity = similarity;
                bestMatch = newType;
            }

            return bestMatch;
        }

        private static double CalculateSimilarity(TypeDefinition oldType, TypeDefinition newType)
        {
            // Some hacks to improve matching success rate
            if (oldType.Name != oldType.Name.ToUpper() && newType.Name != newType.Name.ToUpper())
            {
                if (oldType.Name != newType.Name) return 0;
            }

            // Define weights for each characteristic
            const double inheritanceWeight = 0.35;
            const double fieldCountWeight = 0.3;
            const double nestedClassesCountWeight = 0.15;
            const double methodsCountWeight = 0.2;
            const double modifiersWeight = 0.3;
            // const double attributeCountWeight = 0.3;
            const double enumFieldNamesWeight = 1;

            var inheritanceScore = CompareInheritance(oldType, newType);
            var fieldCountScore = CompareFieldCount(oldType, newType);
            var nestedClassesCountScore = CompareNestedClassesCount(oldType, newType);
            var methodsCountScore = CompareMethodsCount(oldType, newType);
            var modifiersScore = CompareModifiers(oldType, newType);
            var attributeCountScore = CompareAttributeCount(oldType, newType);
            var enumFieldNamesScore = CompareEnumFieldNames(oldType, newType);

            var similarity = inheritanceWeight * inheritanceScore +
                             fieldCountWeight * fieldCountScore +
                             nestedClassesCountWeight * nestedClassesCountScore +
                             methodsCountWeight * methodsCountScore +
                             modifiersWeight * modifiersScore +
                             enumFieldNamesWeight * enumFieldNamesScore;

            return similarity;
        }

        private static double CompareInheritance(TypeDefinition newType, TypeDefinition oldType)
        {
            if (oldType.BaseType == null && newType.BaseType == null) return 1.0;

            if (oldType.BaseType?.FullName == newType.BaseType?.FullName) return 1.0;

            if (oldType.BaseType == null || newType.BaseType == null) return 0.0;

            var baseTypesMatch = false;

            try
            {
                if (!typeMapping.TryGetValue(oldType.BaseType.FullName, out var mappedBaseType) || mappedBaseType == null)
                    return baseTypesMatch ? 1.0 : 0.0;
                var newBaseType = newType.BaseType.FullName;

                if (newBaseType != null && mappedBaseType == newBaseType) baseTypesMatch = true;

                return baseTypesMatch ? 1.0 : 0.0;
            }
            catch (Exception)
            {
                return 0.0;
            }
        }

        private static double CompareFieldCount(TypeDefinition oldType, TypeDefinition newType)
        {
            var originalFieldCount = oldType.Fields.Count;
            var obfuscatedFieldCount = newType.Fields.Count;

            if (originalFieldCount == 0 && obfuscatedFieldCount == 0) return 1.0;

            var fieldCountSimilarity = 0.0;

            if (originalFieldCount != 0 || obfuscatedFieldCount != 0)
                fieldCountSimilarity = (double)Math.Min(originalFieldCount, obfuscatedFieldCount) /
                                       Math.Max(originalFieldCount, obfuscatedFieldCount);

            return fieldCountSimilarity;
        }

        private static double CompareNestedClassesCount(TypeDefinition oldType, TypeDefinition newType)
        {
            var oldNestedClassesCount = oldType.NestedTypes.Count;
            var newNestedClassesCount = newType.NestedTypes.Count;

            if (oldNestedClassesCount == 0 && newNestedClassesCount == 0) return 1.0;

            var nestedClassesCountSimilarity = 0.0;

            if (oldNestedClassesCount != 0 || newNestedClassesCount != 0)
                nestedClassesCountSimilarity = (double)Math.Min(oldNestedClassesCount, newNestedClassesCount) /
                                               Math.Max(oldNestedClassesCount, newNestedClassesCount);

            return nestedClassesCountSimilarity;
        }

        private static double CompareMethodsCount(TypeDefinition oldType, TypeDefinition newType)
        {
            var oldMethodsCount = oldType.Methods.Count;
            var newMethodsCount = newType.Methods.Count;

            return oldMethodsCount == newMethodsCount || (oldMethodsCount > 1 && oldMethodsCount <= newMethodsCount) ? 1.0 : 0.0;
        }

        private static double CompareModifiers(TypeDefinition oldType, TypeDefinition newType)
        {
            var originalIsPublic = oldType.IsPublic;
            var obfuscatedIsPublic = newType.IsPublic;

            var originalIsNotPublic = oldType.IsNotPublic;
            var obfuscatedIsNotPublic = newType.IsNotPublic;

            var originalIsInternal = oldType.IsNestedAssembly;
            var obfuscatedIsInternal = newType.IsNestedAssembly;

            var originalIsAbstract = oldType.IsAbstract;
            var obfuscatedIsAbstract = newType.IsAbstract;

            var originalIsStatic = oldType.IsSealed && oldType.IsAbstract;
            var obfuscatedIsStatic = newType.IsSealed && newType.IsAbstract;

            if (originalIsPublic && obfuscatedIsPublic &&
                originalIsAbstract == obfuscatedIsAbstract &&
                originalIsStatic == obfuscatedIsStatic)
                return 1.0;

            if (originalIsNotPublic && obfuscatedIsNotPublic &&
                originalIsAbstract == obfuscatedIsAbstract &&
                originalIsStatic == obfuscatedIsStatic)
                return 1.0;

            if (originalIsInternal && obfuscatedIsInternal &&
                originalIsAbstract == obfuscatedIsAbstract &&
                originalIsStatic == obfuscatedIsStatic)
                return 1.0;

            return 0.0;
        }

        private static double CompareAttributeCount(ICustomAttributeProvider oldType, ICustomAttributeProvider newType)
        {
            var oldAttributeCount = oldType.CustomAttributes.Count;
            var newAttributeCount = newType.CustomAttributes.Count;

            if (oldAttributeCount == 0 && newAttributeCount == 0)
                return 0.0;

            var attributeCountSimilarity = 0.0;

            if (oldAttributeCount != 0 || newAttributeCount != 0)
                attributeCountSimilarity = (double)Math.Min(oldAttributeCount, newAttributeCount) /
                                           Math.Max(oldAttributeCount, newAttributeCount);

            return attributeCountSimilarity;
        }

        private static double CompareEnumFieldNames(TypeDefinition oldType, TypeDefinition newType)
        {
            if (oldType.BaseType?.FullName != "System.Enum" || newType.BaseType?.FullName != "System.Enum") return 0.0;
            var commonFieldCount = 0;
            var index = 0;
            for (; index < oldType.Fields.Count; index++)
            {
                var field = oldType.Fields[index];
                if (!field.IsStatic || !field.IsLiteral) continue;
                var matchingField =
                    newType.Fields.FirstOrDefault(f => f.Name == field.Name && f.IsStatic && f.IsLiteral);
                if (matchingField != null)
                    commonFieldCount++;
            }

            var similarity = (double)commonFieldCount / Math.Max(oldType.Fields.Count, newType.Fields.Count);
            return similarity;
        }

        static void SetCilBodyKeepOldMaxStack(MethodDef method)
        {
            if (method.Body != null && method.Body.HasInstructions)
            {
                var cilBody = method.Body;
                cilBody.KeepOldMaxStack = true;
            }
        }

        static string? GetDeobfuscatedValue(Dictionary<string, string> mapping, string obfuscatedString)
        {
            if (mapping.ContainsKey(obfuscatedString))
            {
                return mapping[obfuscatedString];
            }

            return null;
        }

        static void PrintHelp()
        {
            Console.WriteLine("Usage: dnMatcher.exe [OPTIONS]\n");
            Console.WriteLine("Options:");
            Print("  -u, --unobf-dll", ConsoleColor.Yellow); Print("    Path to the unobfuscated DLL file"); Print(" (required)\n", ConsoleColor.Yellow);
            Print("  -d, --dll", ConsoleColor.Yellow); Print("          Path to the obfuscated DLL file"); Print(" (required)\n", ConsoleColor.Yellow);
            Print("  -m, --mapping", ConsoleColor.Yellow); Print("      Path to the output mapping file"); Print(" (required)\n", ConsoleColor.Yellow);
            Print("  -o, --output", ConsoleColor.Yellow); Print("       Path to the output file"); Print(" (required)\n", ConsoleColor.Yellow);
            Console.WriteLine("  --debug            Enable debug logging");
            Console.WriteLine("  --ignore-errors    Ignore errors during deobfuscation (recommended for Cpp2Il DLLs)");
            Console.WriteLine("  --minified-mapping Skip repeated names on both sides of the mapping");
            Console.WriteLine("  --help             Display this help message");
        }

        static void Debug(string message, ConsoleColor? color = null)
        {
            if (debugEnabled)
            {
                ConsoleColor previousColor = Console.ForegroundColor;

                if (color.HasValue)
                    Console.ForegroundColor = color.Value;

                Console.WriteLine("[DEBUG] " + message);
                Console.ForegroundColor = previousColor;
            }
        }

        static void Print(string message, ConsoleColor? color = null)
        {
            ConsoleColor previousColor = Console.ForegroundColor;

            if (color.HasValue)
                Console.ForegroundColor = color.Value;

            Console.Write(message);
            Console.ForegroundColor = previousColor;
        }
    }
}