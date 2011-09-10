using System;
using System.Reflection;
using System.Reflection.Emit;
using Niecza;
using System.Collections.Generic;
using System.Threading;

namespace Niecza {
    class CLROpts {
        public static readonly bool Debug =
            Environment.GetEnvironmentVariable("NIECZA_CLR_TRACE") != null;
        public static readonly bool MMDDebug =
            Environment.GetEnvironmentVariable("NIECZA_MMD_TRACE") != null;
    }

    // These classes implement the basic C# multiple dispatch algorithm:
    // Candidates form a poset.  Select the greatest element of the subset
    // filtered by admissability of the actual arguments.
    //
    // The actual algorithm starts by topologically sorting the candidates
    // $C_i$, that is, assigning $i$ values such that
    // $C_i > C_j \rightarrow i > j$.  Assume there are no nontrivial equal
    // elements in the ordering.
    //
    // Now suppose $C_n$ is the last
    // admissible candidate (if there is none, then there is trivially no
    // greatest element).
    //
    // If there is a greatest element, then it is $C_n$.  Proof.  Suppose
    // $C_m$ is the greatest element.  $C_m > C_i$ for any admissable $i$,
    // therefore $m >t i$ and $m$ is the last admissable index, therefore
    // equal to $n$.
    //
    // So all that remains is to check if $C_m$ is actually the greatest
    // element, that is to check that $C_p$ is inadmissable for all $p$ where
    // $C_p \nless C_m$.  The case $p > m$ is already known; we keep a list
    // of possible conflictors, values of $p < m$ for all $m$.

    abstract class MultiCandidate {
        public abstract bool Admissable(Variable[] pos, VarHash named);
        public abstract int  Compare(int arity, MultiCandidate other);
        public abstract bool AdmissableArity(int arity);
        public abstract int  MinDispatchArity();

        internal int[] conflictors;
    }

    class CandidateSet {
        MultiCandidate[][] cands;
        MultiCandidate[] orig;
        string name;

        public CandidateSet(string name, MultiCandidate[] orig) {
            this.orig = orig;
            this.name = name;
            int max_arity = 0;
            foreach (MultiCandidate mc in orig) {
                int mda = mc.MinDispatchArity();
                if (mda > max_arity) max_arity = mda;
            }
            cands = new MultiCandidate[max_arity+1][];
        }

        MultiCandidate[] GetCandidateList(int arity) {
            if (arity > cands.Length - 1)
                arity = cands.Length - 1;

            if (cands[arity] == null) {
                MultiCandidate[] n = SortCandidates(arity);
                // this is needed to ensure memory write ordering IIUC
                Interlocked.CompareExchange(ref cands[arity], n, null);
                return n;
            } else {
                return cands[arity];
            }
        }

        // throws on dispatch failure
        public MultiCandidate DoDispatch(Variable[] pos, VarHash named) {
            MultiCandidate[] avail = GetCandidateList(pos.Length);

            int last_ix;
            for (last_ix = avail.Length - 1; last_ix >= 0; last_ix--)
                if (avail[last_ix].Admissable(pos, named))
                    break;

            if (last_ix < 0) {
                throw new NieczaException("No candidates for dispatch to " + name +
                    "; candidates are:" + Console.Out.NewLine + "    " +
                    Kernel.JoinS(Console.Out.NewLine + "    ", avail));
            }

            foreach (int ci in avail[last_ix].conflictors) {
                if (avail[ci].Admissable(pos, named)) {
                    List<MultiCandidate> matched = new List<MultiCandidate>();

                    foreach (MultiCandidate mc in avail)
                        if (mc.Admissable(pos, named))
                            matched.Add(mc);

                    throw new NieczaException("Ambiguous dispatch for " + name +
                        "; matched candidates are:" + Console.Out.NewLine + "    " +
                        Kernel.JoinS(Console.Out.NewLine + "    ", matched));
                }
            }

            if (CLROpts.MMDDebug)
                Console.WriteLine("Using {0}", avail[last_ix]);

            return avail[last_ix];
        }

        MultiCandidate[] SortCandidates(int arity) {
            List<MultiCandidate> afilt = new List<MultiCandidate>();
            foreach (MultiCandidate mc in orig)
                if (mc.AdmissableArity(arity))
                    afilt.Add(mc);
            int n = afilt.Count;
            bool[] gt = new bool[n*n];
            int[] blocks  = new int[n]; // # of unused elements less than i
            int[] reorder = new int[n];

            for (int i = 0; i < n; i++) {
                for (int j = 0; j < i; j++) {
                    int comp = afilt[i].Compare(arity, afilt[j]);
                    if (comp > 0) { // $C_i > C_j$
                        gt[i*n+j] = true;
                        blocks[i]++;
                    } else if (comp < 0) {
                        gt[j*n+i] = true;
                        blocks[j]++;
                    }
                }
            }

            int assigned = 0;
            while (assigned != n) {
                int i;
                for (i = 0; i < n; i++)
                    if (blocks[i] == 0)
                        break;
                reorder[assigned++] = i;
                for (int j = 0; j < n; j++)
                    if (gt[j*n + i]) blocks[j]--;
                blocks[i] = int.MaxValue;
            }

            MultiCandidate[] ret = new MultiCandidate[n];

            for (int i = 0; i < n; i++) {
                ret[i] = afilt[reorder[i]];
                List<int> conflicts = new List<int>();
                for (int j = 0; j < i; j++) {
                    if (!gt[reorder[i]*n + reorder[j]])
                        conflicts.Add(j);
                }
                ret[i].conflictors = conflicts.ToArray();
            }

            if (CLROpts.MMDDebug) {
                Console.WriteLine("--- MMD CANDIDATE SORT ORDER ---");
                for (int i = 0; i < n; i++) {
                    Console.WriteLine("{0}: {1}", i, ret[i]);
                    Console.WriteLine("     c: {0}", Kernel.JoinS(" ", ret[i].conflictors));
                }
            }

            return ret;
        }
    }

    class OverloadCandidate : MultiCandidate {
        MethodBase what_call;
        Type[] args;
        Type param_array;

        private OverloadCandidate(MethodBase what_call, Type[] args,
                Type param_array) {
            this.what_call = what_call;
            this.args = args;
            this.param_array = param_array;
        }

        public static void GenerateCandidates(MethodBase what,
                List<MultiCandidate> into) {
            ParameterInfo[] pi = what.GetParameters();
            Type[] args1 = new Type[pi.Length];
            for (int i = 0; i < pi.Length; i++)
                args1[i] = pi[i].ParameterType;
            into.Add(new OverloadCandidate(what, args1, null));

            if (pi.Length != 0 && pi[pi.Length-1].GetCustomAttributes(
                        typeof(ParamArrayAttribute), false).Length != 0) {
                Type[] args2 = new Type[args1.Length - 1];
                Array.Copy(args1, 0, args2, 0, args2.Length);
                into.Add(new OverloadCandidate(what, args2,
                            args1[args1.Length - 1].GetElementType()));
            }
        }

        public override string ToString() {
            string s1 = Kernel.JoinS(", ", args);
            if (param_array != null) {
                return s1 + (s1 == "" ? "params " : ", params ") + param_array + "[]";
            } else {
                return s1;
            }
        }

        public Variable Invoke(object obj, Variable[] pos, VarHash named) {
            object[] argv = new object[args.Length +
                (param_array != null ? 1 : 0)];
            for (int i = 0; i < args.Length; i++)
                CoerceArgument(out argv[i], args[i], pos[i]);
            if (param_array != null) {
                int npa = pos.Length - args.Length;
                Array pa = Array.CreateInstance(param_array, npa);
                for (int j = 0; j < npa; j++) {
                    object arg;
                    CoerceArgument(out arg, param_array, pos[j + args.Length]);
                    pa.SetValue(arg, j);
                }
                argv[args.Length] = pa;
            }
            object ret = what_call.Invoke((what_call.IsStatic ? null : obj),
                argv);
            if (what_call is MethodInfo) {
                return BoxResult(((MethodInfo)what_call).ReturnType, ret);
            } else {
                return BoxResult(what_call.DeclaringType, ret);
            }
        }

        public static Variable BoxResult(Type cty, object ret) {
            if (cty == typeof(void))
                return Kernel.NewRWListVar(Kernel.NilP);
            if (cty == typeof(sbyte))
                return Builtins.MakeInt((sbyte)ret);
            if (cty == typeof(byte))
                return Builtins.MakeInt((byte)ret);
            if (cty == typeof(short))
                return Builtins.MakeInt((short)ret);
            if (cty == typeof(ushort))
                return Builtins.MakeInt((ushort)ret);
            if (cty == typeof(int))
                return Builtins.MakeInt((int)ret);
            if (cty == typeof(uint))
                return Builtins.MakeInt((uint)ret);
            if (cty == typeof(long))
                return Builtins.MakeInt((long)ret);
            if (cty == typeof(ulong))
                return Builtins.MakeInt((ulong)ret);
            if (cty == typeof(float))
                return Builtins.MakeFloat((float)ret);
            if (cty == typeof(double))
                return Builtins.MakeFloat((double)ret);
            if (cty == typeof(bool))
                return Kernel.BoxAnyMO((bool)ret, Kernel.BoolMO);
            if (cty == typeof(string))
                return Kernel.BoxAnyMO((string)ret, Kernel.StrMO);
            if (cty == typeof(Variable))
                return (Variable)ret;
            if (cty == typeof(P6any))
                return Kernel.NewROScalar((P6any)ret);

            return Kernel.BoxAnyMO<object>(ret, null); // XXX
        }

        public override bool Admissable(Variable[] pos, VarHash named) {
            if (named != null && named.IsNonEmpty)
                return false;
            if (!AdmissableArity(pos.Length))
                return false;

            object dummy;
            for (int i = 0; i < args.Length; i++)
                if (!CoerceArgument(out dummy, args[i], pos[i]))
                    return false;
            // XXX: maybe param arrays should be treated as slurpies?
            for (int i = args.Length; i < pos.Length; i++)
                if (!CoerceArgument(out dummy, param_array, pos[i]))
                    return false;

            return true;
        }

        bool CoerceArgument(out object clr, Type ty, Variable var) {
            P6any obj = var.Fetch();
            clr = null;

            // type objects are typed nulls
            if (!obj.IsDefined()) {
                if (obj is BoxObject<object>) {
                    Type t = (Type)Kernel.UnboxAny<object>(obj.mo.how);
                    // is this enough?
                    return (ty.IsAssignableFrom(t) && !ty.IsValueType &&
                            ty != typeof(void));
                } else if (obj.mo == Kernel.MuMO || obj.mo == Kernel.AnyMO) {
                    // untyped-ish null
                    return !ty.IsValueType && ty != typeof(void);
                } else {
                    // we'll pass this by value anyway
                    clr = obj;
                    return ty.IsAssignableFrom(obj.GetType());
                }
            }
            // in all other cases we're definitely passing a non-null value

            // Boolean values marshal to bool
            if (obj.Does(Kernel.BoolMO)) {
                clr = Kernel.UnboxAny<bool>(obj);
            }
            // note, Bool ~~ Int ~~ Integral
            else if (obj.Does(Kernel.IntegralMO)) {
                // important type directed case!
                int small;
                BigInteger big;
                bool use_big = Builtins.GetAsInteger(var, out small, out big);

                if (ty == typeof(sbyte))
                    clr = (!use_big && small >= sbyte.MinValue && small <= sbyte.MaxValue) ? (object)(sbyte)small : null;
                else if (ty == typeof(byte))
                    clr = (!use_big && small >= byte.MinValue && small <= byte.MaxValue) ? (object)(byte)small : null;
                else if (ty == typeof(short))
                    clr = (!use_big && small >= short.MinValue && small <= short.MaxValue) ? (object)(short)small : null;
                else if (ty == typeof(ushort))
                    clr = (!use_big && small >= ushort.MinValue && small <= ushort.MaxValue) ? (object)(ushort)small : null;
                else {
                    big = use_big ? big : (BigInteger) small;

                    if (ty == typeof(int))
                        clr = (big >= int.MinValue && big <= int.MaxValue) ? (object)(int)big : null;
                    else if (ty == typeof(uint))
                        clr = (big >= uint.MinValue && big <= uint.MaxValue) ? (object)(uint)big : null;
                    else if (ty == typeof(long))
                        clr = (big >= long.MinValue && big <= long.MaxValue) ? (object)(long)big : null;
                    else if (ty == typeof(ulong))
                        clr = (big >= ulong.MinValue && big <= ulong.MaxValue) ? (object)(ulong)big : null;

                    else if (ty == typeof(float))
                        clr = (object)(float)big;
                    else if (ty == typeof(double))
                        clr = (object)(double)big;
                    else if (ty == typeof(decimal))
                        clr = big.GetWords().Length <= 3 ? (object)(decimal)big : null;
                    else
                        clr = obj;
                }
            }
            else if (obj.Does(Kernel.StrMO)) {
                string s = Kernel.UnboxAny<string>(obj);
                if (ty == typeof(char) && s.Length == 1)
                    clr = s[0];
                else if (ty == typeof(string))
                    clr = s;
                else
                    clr = obj;
            }
            // TODO: Code to delegates, Array to IList(maybe)
            else {
                clr = obj;
            }

            return clr != null && ty.IsAssignableFrom(clr.GetType());
        }

        public override int  Compare(int arity, MultiCandidate other_) {
            bool any_better = false, any_worse = false;
            OverloadCandidate other = (OverloadCandidate) other_;

            for (int ix = 0; ix < arity; ix++) {
                int res = CompareType(ix >= args.Length ? param_array : args[ix],
                    ix >= other.args.Length ? other.param_array : other.args[ix]);
                if (res > 0) any_better = true;
                if (res < 0) any_worse  = true;
            }

            if (any_better && !any_worse)  return 1;
            if (any_worse  && !any_better) return -1;
            return 0;
        }

        const int NUM_NUMTYPES = 12;
        static Type[] num_types = new Type[] {
            typeof(sbyte), typeof(byte), typeof(short), typeof(ushort),
            typeof(int), typeof(uint), typeof(long), typeof(ulong),
            typeof(char), typeof(float), typeof(double), typeof(decimal),
        };

        // +1 if Y is a signed type shorter-or-equal to unsigned X, or
        // Y is implicitly convertable to X
        static sbyte[,] num_preced = new sbyte[,] {
            //sb  ub  ss  us  si  ui  sl  ul  ch  sf  df  dc
            {  0,  1,  1,  1,  1,  1,  1,  1,  0,  1,  1,  1 }, //sbyte
            {  0,  0,  1,  1,  1,  1,  1,  1,  0,  1,  1,  1 }, //byte
            {  0,  0,  0,  1,  1,  1,  1,  1,  0,  1,  1,  1 }, //short
            {  0,  0,  0,  0,  1,  1,  1,  1,  0,  1,  1,  1 }, //ushort
            {  0,  0,  0,  0,  0,  1,  1,  1,  0,  1,  1,  1 }, //int
            {  0,  0,  0,  0,  0,  0,  1,  1,  0,  1,  1,  1 }, //uint
            {  0,  0,  0,  0,  0,  0,  0,  1,  0,  1,  1,  1 }, //long
            {  0,  0,  0,  0,  0,  0,  0,  0,  0,  1,  1,  1 }, //ulong
            {  0,  0,  0,  1,  1,  1,  1,  1,  0,  1,  1,  1 }, //char
            {  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  1,  0 }, //float
            {  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0 }, //double
            {  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0 }, //decimal
        };

        int CompareType(Type t1, Type t2) {
            int i1, i2;
            if (t1 == t2) return 0;
            for (i1 = 0; i1 < NUM_NUMTYPES && t1 != num_types[i1]; i1++) ;
            for (i2 = 0; i2 < NUM_NUMTYPES && t2 != num_types[i2]; i2++) ;

            if (i1 != NUM_NUMTYPES && i2 != NUM_NUMTYPES)
                return num_preced[i1,i2] - num_preced[i2,i1];

            if (t1.IsAssignableFrom(t2))
                return 1;
            if (t2.IsAssignableFrom(t1))
                return -1;
            return 0;
        }

        public override bool AdmissableArity(int arity) {
            return param_array == null ? arity == args.Length :
                arity >= args.Length;
        }

        public override int  MinDispatchArity() {
            return args.Length + 1;
        }
    }

    class OverloadDispatcher {
    }

    // public class NieczaCLR {
    //     static Dictionary<Type, STable> wrapper_cache
    //         = new Dictionary<Type, STable>();
    // 
    //     static STable GetWrapper(Type t) {
    //         lock (wrapper_cache) {
    //             STable r;
    //             if (wrapper_cache.TryGetValue(t, out r))
    //                 return r;
    //             return wrapper_cache[t] = NewWrapper(t);
    //         }
    //     }
    // 
    //     static STable NewWrapper(Type t) {
    //         STable m = new STable("clr:" + t.FullName);
    //         STable pm = t.BaseType == null ? Kernel.AnyMO :
    //             GetWrapper(t.BaseType);
    //         STable[] mro = new STable[pm.mo.mro.Length + 1];
    //         Array.Copy(pm.mo.mro, 0, mro, 1, pm.mo.mro.Length);
    //         mro[0] = m;
    //         m.FillClass(new string[] { }, new STable[] { pm }, mro);
    //         //m.loc_to_clr = CLRToCLR.Instance;
    //         if (NieczaCLROpts.Debug)
    //             Console.WriteLine("Setting up wrapper for {0}", t.FullName);
    // 
    //         HashSet<string> needNewWrapper = new HashSet<string>();
    //         Dictionary<string,List<MethodInfo>> allMethods
    //             = new Dictionary<string,List<MethodInfo>>();
    //         Dictionary<string,List<PropertyInfo>> allProperties
    //             = new Dictionary<string,List<PropertyInfo>>();
    // 
    //         foreach (MethodInfo mi in t.GetMethods(BindingFlags.Public |
    //                     BindingFlags.Static | BindingFlags.Instance)) {
    //             if (NieczaCLROpts.Debug)
    //                 Console.WriteLine("Checking method : {0}", mi);
    //             if (mi.GetBaseDefinition().DeclaringType == t && !mi.IsSpecialName)
    //                 needNewWrapper.Add(mi.Name);
    //             MultiAdd(allMethods, mi.Name, mi);
    //         }
    // 
    //         foreach (PropertyInfo pi in t.GetProperties(BindingFlags.Public |
    //                     BindingFlags.Static | BindingFlags.Instance)) {
    //             if (NieczaCLROpts.Debug)
    //                 Console.WriteLine("Checking property : {0}", pi);
    //             MethodInfo[] mis = pi.GetAccessors();
    //             if (mis.Length != 0 && mis[0].GetBaseDefinition().DeclaringType == t)
    //                 needNewWrapper.Add(pi.Name);
    //             MultiAdd(allProperties, pi.Name, pi);
    //         }
    // 
    //         List<int> handlers = new List<int>();
    //         foreach (string n in needNewWrapper) {
    //             handlers.Clear();
    // 
    //             string siname = string.Format("wrapper {0}:{1}", t.FullName, n);
    // 
    //             if (allMethods.ContainsKey(n))
    //                 handlers.Add(1);
    //             if (allProperties.ContainsKey(n))
    //                 handlers.Add(2);
    // 
    //             DynBlockDelegate method = null;
    // 
    //             switch (handlers.Count == 1 ? handlers[0] : 0) {
    //                 case 0:
    //                     method = BindAmbiguous(siname, t, n);
    //                     break;
    // 
    //                 case 1:
    //                     method = BindMethodGroup(siname, t, n, allMethods[n]);
    //                     break;
    // 
    //                 case 2:
    //                     method = BindPropertyGroup(siname, t, n, allProperties[n]);
    //                     break;
    //             }
    // 
    //             if (NieczaCLROpts.Debug)
    //                 Console.WriteLine("Installing {0}", siname);
    //             m.AddMethod(0,n, Kernel.MakeSub(new SubInfo(siname, method), null));
    //         }
    // 
    //         m.Invalidate();
    //         m.typeObject = new BoxObject<object>(null, m);
    //         ((P6opaque)m.typeObject).slots = null;
    //         return m;
    //     }
    // 
    //     public static Variable BoxCLR(object o) {
    //         if (o == null)
    //             return Kernel.NewROScalar(Kernel.AnyP);
    //         return Kernel.NewROScalar(new BoxObject<object>(o,
    //                     GetWrapper(o.GetType())));
    //     }
    // 
    //     public static Variable GetClass(string lang, string from) {
    //         lang = lang.ToLowerInvariant();
    //         // TODO: Microsoft.ScriptEngine interop
    //         if (lang != "clr" && lang != "cls" && lang != "dotnet")
    //             throw new NieczaException("Unknown source language " + lang);
    // 
    //         string e = Environment.GetEnvironmentVariable("NIECZA_WRAP_CLASS");
    //         if (e != null)
    //             GetWrapper(Type.GetType(from, true));
    // 
    //         return Kernel.NewROScalar(GetWrapper(Type.GetType(from, true)).typeObject);
    //     }
    // }
}

public partial class Builtins {
    public static Variable clr_test() {
        List<MultiCandidate> ocs = new List<MultiCandidate>();
        foreach (MethodInfo mi in typeof(Console).GetMethods()) {
            if (mi.Name == "WriteLine")
                OverloadCandidate.GenerateCandidates(mi, ocs);
        }
        CandidateSet cs = new CandidateSet("System::Console.WriteLine", ocs.ToArray());

        Variable[] pos = new Variable[] { Kernel.BoxAnyMO("Hello, world", Kernel.StrMO) };
        OverloadCandidate oc = (OverloadCandidate)cs.DoDispatch(pos, null);
        return oc.Invoke(null, pos, null);
    }
}

