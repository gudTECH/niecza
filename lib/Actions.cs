using System;
using System.Collections.Generic;

// Public methods with the same name as a STD rule are assumed to be handlers
// for that rule.

namespace Niecza.Compiler {
    class Actions {
        internal CompJob job;
        RxOp.RxOp rnoop = new RxOp.Sequence(new RxOp.RxOp[0]);
        public Actions(CompJob job) { this.job = job; }

        // Little things to make writing wads of data-extraction code nicer...
        string asstr(Variable v) { return v.Fetch().mo.mro_raw_Str.Get(v); }
        bool istrue(Variable v) { return v.Fetch().mo.mro_raw_Bool.Get(v); }
        bool isdef(Variable v) { return v.Fetch().mo.mro_raw_defined.Get(v); }
        Variable atk(Variable v, string at) {
            return v.Fetch().mo.mro_at_key.Get(v, mkstr(at));
        }
        Variable atp(Variable v, int at) {
            return v.Fetch().mo.mro_at_pos.Get(v, mkint(at));
        }

        P6any mkint(int i) { return (P6any)Builtins.MakeInt(i); }
        P6any mkstr(string s) { return s==null ? Kernel.StrMO.typeObj : (P6any)Builtins.MakeStr(s); }

        P6any plus(Variable v1,Variable v2) { return (P6any)Builtins.plus(v1,v2); }
        P6any minus(Variable v1,Variable v2) { return (P6any)Builtins.minus(v1,v2); }
        P6any mul(Variable v1,Variable v2) { return (P6any)Builtins.mul(v1,v2); }
        P6any div(Variable v1,Variable v2) { return (P6any)Builtins.divide(v1,v2); }
        P6any pow(Variable v1,Variable v2) { return (P6any)Builtins.pow(v1,v2); }
        void assign(Variable a, Variable b) { Kernel.Assign(a,b); }

        // SSTO TODO integrate with parser
        void sorry(Cursor m, string fmt, params object[] args) {
            throw new NotImplementedException();
        }
        STable compile_get_pkg(bool auto, params string[] path) {
            throw new NotImplementedException();
        }
        void addlex(Cursor pos, SubInfo to, string name, LexInfo li) {
            throw new NotImplementedException();
        }
        void trymop(Cursor m, Action a) {
            try {
                a();
            } catch (Exception e) {
                sorry(m, e.Message);
            }
        }

        T ast<T>(Variable v) {
            return (T)Kernel.UnboxAny<object>(((Cursor)v.Fetch()).ast);
        }
        void make(Cursor m, object val) {
            m.ast = Kernel.BoxRaw(val, Kernel.AnyMO);
        }

        Variable[] flist(Variable list) {
            // we force the list to iterate here
            VarDeque iter = Builtins.start_iter(list.Fetch().mo.mro_list.Get(list));
            VarDeque into = new VarDeque();
            while (Kernel.IterHasFlat(iter, true))
                into.Push(iter.Shift());
            return into.CopyAsArray();
        }

        B[] map<A,B>(Func<A,B> fn, A[] args) {
            B[] ret = new B[args.Length];
            for (int i = 0; i < args.Length; i++)
                ret[i] = fn(args[i]);
            return ret;
        }

        // SSTO TODO: how are we handling categoricals?  FALLBACK

        // TODO Merge with the corresponding setting/runtime code?
        // using p6numbers because we need bignum support here
        // TODO Unicode
        P6any from_base(Cursor m, Variable str, int based_) {
            var acc = mkint(0);
            var based = mkint(based_);
            int punto = -1;

            foreach (char ch_ in asstr(str)) {
                var ch = char.ToLower(ch_);
                if (ch == '_') continue;
                if (ch == '.') { punto = 0; continue; }
                if (punto >= 0) punto++;
                var digit = ch >= 'a' ? ((int)ch) - 87 : ((int)ch) - 48;
                if (digit >= based_)
                    sorry(m, "Digit <{0}> too large for radix {1}", ch, based_);
                acc = plus(mkint(digit), mul(based, acc));
            }
            return punto < 0 ? acc : div(acc, pow(based, mkint(punto)));
        }

        public void decint(Cursor m) { make(m,from_base(m, m, 10)); }
        public void hexint(Cursor m) { make(m,from_base(m, m, 16)); }
        public void octint(Cursor m) { make(m,from_base(m, m, 8)); }
        public void binint(Cursor m) { make(m,from_base(m, m, 2)); }
        public void integer(Cursor m) {
            Variable v = null;
            if (isdef(v = atk(m, "decint")) || isdef(v = atk(m, "octint")) ||
                    isdef(v = atk(m, "hexint")) || isdef(v = atk(m, "binint")))
                make(m,ast<P6any>(v));
        }

        public void decints(Cursor m) { make(m, map(v => ast<P6any>(v), flist(atk(m,"decint")))); }
        public void hexints(Cursor m) { make(m, map(v => ast<P6any>(v), flist(atk(m,"hexint")))); }
        public void octints(Cursor m) { make(m, map(v => ast<P6any>(v), flist(atk(m,"octint")))); }
        public void binints(Cursor m) { make(m, map(v => ast<P6any>(v), flist(atk(m,"binint")))); }

        public void dec_number(Cursor m) {
            if (istrue(atk(m,"escale"))) {
                // SSTO use the real number parser here
                make(m, (P6any)Builtins.MakeFloat(
                            Utils.S2N(asstr(m).Replace("_",""))));
            } else make(m,from_base(m,m,10));
        }

        public void radint(Cursor m) {
            Variable var = null;
            if (isdef(var = atk(m,"rad_number")) || isdef(var = atk(m,"integer")))
                make(m,ast<P6any>(var));
        }

        public void rad_number(Cursor m) { // returns Op *or* P6any ...
            int radix = (int)Utils.S2N(asstr(atk(m,"radix")));

            var f = atk(m,"circumfix");
            if (istrue(f)) {
                // STD guarantees this can never happen from within radint;
                // only number can see this
                make(m, Op.Helpers.mkcall(m, "&unbase", new Op.Num(m,
                    new object[] { 10, radix.ToString() }), ast<Op.Op>(f)));
                return;
            }
            var value = istrue(f=atk(m,"int")) ? from_base(m,f,radix):mkint(0);

            if (istrue(f = atk(m,"frac"))) {
                var shift = asstr(f).Replace("_","").Length;
                value = plus(value, div(from_base(m, f, radix),
                            pow(mkint(radix), mkint(shift))));
            }
            if (istrue(f = atk(m,"base"))) {
                value = mul(value, pow(ast<P6any>(f), istrue(atk(m,"exp")) ?
                    ast<P6any>(atk(m,"exp")) : mkint(0)));
                value = (P6any)Builtins.coerce_to_num(value);
                // exponential notation is always imprecise here
            }
            make(m,value);
        }

        public void number(Cursor m) {
            var child = atk(m,"integer");
            if (!isdef(child)) child = atk(m,"dec_number");
            if (!isdef(child)) child = atk(m,"rad_number");
            object val;
            if (!isdef(child)) {
                val = asstr(m) == "NaN" ? double.NaN : double.PositiveInfinity;
            } else {
                val = ast<object>(child);
            }
            make(m, val is Op.Op ? val : new Op.Const(m,(P6any)val));
        }

        public void charname(Cursor m) {
            var radint = atk(m,"radint");
            string res;
            if (istrue(radint)) {
                var ast = ast<P6any>(radint);
                int val = (int)Builtins.ToNum(ast);
                if (!ast.Isa(Kernel.IntMO) || val < 0 || val > 0x10FFFF) {
                    res = " ";
                    sorry(m, "Numeric character identifiers must be integers between 0 and 0x10FFFF");
                } else {
                    res = new string((char) val, 1);
                }
            } else {
                res = " ";
                trymop(m, () => { res = Niecza.UCD.DataSet.GetCodepoint(asstr(m)); });
            }
            make(m, mkstr(res));
        }

        public void charnames(Cursor m) {
            var sb = new System.Text.StringBuilder();
            foreach (var charname in flist(atk(m,"charname")))
                sb.Append(ast<string>(charname));
            make(m,sb.ToString());
        }

        public void charspec(Cursor m) {
            if (istrue(atk(m,"charnames"))) {
                make(m,ast<string>(atk(m,"charnames")));
            } else {
                var str = asstr(m);
                if (CC.Digit.Accepts(str[0])) {
                    make(m, asstr(Builtins.chr(from_base(m,m,10))));
                } else {
                    make(m, new string((char) (str[0] & 31), 1));
                }
            }
        }

        public void value__number(Cursor m) { make(m,ast<Op.Op>(atk(m,"number"))); }
        public void value__quote(Cursor m) { make(m,ast<Op.Op>(atk(m,"quote"))); }

        public void label(Cursor m) {
            trymop(m, () => {
                addlex(m, job.curlex, asstr(atk(m,"identifier")), new LILabel());
            });
            make(m, asstr(atk(m,"identifier")));
        }

        public void morename(Cursor m) { // Either String Op.Op
            Variable v = null;
            if (istrue(v = atk(m,"identifier"))) make(m,asstr(v));
            else if (istrue(v = atk(m,"EXPR"))) make(m,ast<Op.Op>(v));
            else make(m,(string)null);
        }

        struct TypeConstraint {
            public P6any value;
            public Op.Op where;
            public int tmode; // if type != null, a SIG_ flag
            public STable type;
        }

        // P6any (value constraint) or Op.Op (where) or 
        public void type_constraint(Cursor m) {
            var tc = default(TypeConstraint);
            if (istrue(atk(m,"value"))) {
                var val = ast<Op.Op>(atk(m,"value")).const_value(job.curlex);
                if (val == null) {
                    sorry(m, "Value constraint is not constant");
                    val = Kernel.AnyP;
                }
                tc.value = val.Fetch();
            } else if (istrue(atk(m,"typename"))) {
                tc = ast<TypeConstraint>(atk(m,"typename"));
            } else {
                tc.where = ast<Op.Op>(atk(m,"EXPR"));
            }
            make(m,tc);
        }

        public void typename(Cursor m) {
            if (istrue(atk(m, "whence")))
                sorry(m, "WHENCE blocks are not allowed on declarative type names");

            var ret = default(TypeConstraint);
            if (istrue(atk(m, "ident"))) {
                sorry(m, "::?CLASS syntax NYI");
                ret.tmode = 0; ret.type = Kernel.AnyMO;
                make(m, ret);
                return;
            }

            var lname = atk(m, "longname");
            ret.type = process_name(lname, REFER).pkg;

            if (ret.type == null) {
                sorry(m, "A type must be provided");
                ret.type = Kernel.AnyMO;
            }

            if (istrue(atk(m, "typename")))
                sorry(m, "Coercive declarations NYI");

            foreach (var cp in flist(atk(lname, "colonpair"))) {
                string scp = asstr(cp);
                int mask = 0;
                if (scp == ":_") mask = Parameter.ANY_DEF;
                if (scp == ":T") mask = Parameter.UNDEF_ONLY;
                if (scp == ":U") mask = Parameter.UNDEF_ONLY;
                if (scp == ":D") mask = Parameter.DEF_ONLY;
                if (mask == 0) continue;
                if (ret.tmode != 0)
                    sorry(m, "You may only specify one of :_ :D :U :T");
                ret.tmode |= mask;
            }

            make(m, ret);
        }

        struct Name {
            public bool dc;
            public object[] names; // Either String Op
        }
        public void name(Cursor m) {
            Name r = default(Name);
            r.names = map(v => ast<object>(v), flist(atk(m,"morename")));
            if (istrue(atk(m,"identifier"))) {
                r.names = Utils.PrependArr(r.names, asstr(atk(m,"identifier")));
                r.dc = true;
            }
            make(m,r);
        }

        bool is_pseudo_pkg(string name) {
            foreach (var s in new [] { "MY", "OUR", "CORE", "DYNAMIC", "GLOBAL",
                "CALLER", "OUTER", "UNIT", "SETTING", "PROCESS", "COMPILING",
                "PARENT", "CLR" })
                if (name == s) return true;
            return false;
        }

        struct VarInfo {
            public STable pkg;
            public string name;
            public Op.Op ind;
        };

        // This is to be the one place where names are processed

        // MODES
        //
        // DECL:  pkg + name used. name == null means no name given.
        // DEFER: pkg + name *or* ind

        // OPTIONS
        // clean:     remove :sym<xyz>
        const int REFER = 0;
        const int DECL  = 1;
        const int DEFER = 2;

        const int CLEAN = 4;
        VarInfo process_name(Variable nm, int mode = 0) {
            var ret = new VarInfo();
            if (!isdef(nm)) {
                return ret;
            }
            Cursor c = (Cursor) nm.Fetch();

            var nast = ast<Name>(atk(nm, "name"));
            var names = nast.names;
            int nlen = names.Length;
            string ext = "";
            bool trail = nlen != 0 && names[nlen - 1] == null;
            if (trail) nlen--;

            if ((mode & CLEAN) == 0) {
                var eb = new System.Text.StringBuilder();
                foreach (var cp in flist(atk(c, "colonpair")))
                    eb.Append(get_cp_ext(cp));
                ext = eb.ToString();
            }

            if ((mode & DEFER) == 0) {
                for (int i = 0; i < nlen; i++) {
                    if (names[i] is Op.Op)
                        names[i] = asstr(eval_ast(c, (Op.Op)names[i]));
                }
            }

            if ((mode & DECL) != 0) {
                // class :: is ... { } is a placeholder for a lack of name
                if (nlen == 0)
                    return ret;
                if (trail)
                    sorry(c, "Illegal explicit declaration of a symbol table");
                if ((mode & DEFER) != 0) throw new NotSupportedException();

                ret.name = (string)names[nlen - 1] + ext;
                if (nlen != 1) {
                    // The remainder is assumed to name an existing or new pkg
                    trymop(c, () => {
                        var sa = new string[nlen];
                        Array.Copy(names,0, sa,0, nlen);
                        ret.pkg = compile_get_pkg(true, sa);
                    });
                }
                return ret;
            }
            else if ((mode & DEFER) != 0) {
                // The stuff returned here is processed by the variable rule,
                // and also by method call generation
                if (trail) goto dyn;
                var tail = new string[nlen-1];
                for (int i = 0; i < nlen-1; i++) {
                    tail[i] = names[i] as string;
                    if (tail[i] == null) goto dyn;
                }
                ret.name = names[nlen-1] as string;
                if (ret.name == null) goto dyn;
                ret.name = ret.name + ext;
                try {
                    ret.pkg = compile_get_pkg(true, tail);
                } catch (Exception) { }
                if (ret.pkg == null) goto dyn;
                return ret;
dyn:
                var bits = new List<Op.Op>();
                for (int i = 0; i < nlen; i++) {
                    bits.Add(names[i] is Op.Op ? (Op.Op)names[i] :
                        new Op.StringLiteral(c,(string)names[i]));
                    if (i != nlen - 1) bits.Add(new Op.StringLiteral(c,"::"));
                }
                if (trail) bits.Add(new Op.StringLiteral(c,"::"));
                bits.Add(new Op.StringLiteral(c,ext));
                ret.name = null;
                ret.ind = Op.Helpers.mkstringycat(c, bits.ToArray());
                return ret;
            }
            else {
                if (trail)
                    sorry(c,"Class required, but symbol table name used instead");
                if (nlen == 0) return ret;
                var tail = new string[nlen];
                Array.Copy(names,0, tail,0, nlen);
                tail[nlen-1] += ext;
                trymop(c, () => {
                    ret.pkg = compile_get_pkg(false, tail);
                });
                return ret;
            }
        }

        public void subshortname(Cursor m) {
            if (istrue(atk(m,"colonpair"))) {
                var n = new System.Text.StringBuilder();
                n.Append(asstr(atk(m,"category")));
                foreach (var cp in flist(atk(m,"colonpair")))
                    n.Append(get_cp_ext(cp));
                var ret = default(VarInfo);
                ret.name = n.ToString();
                make(m,ret);
            } else {
                make(m,ast<VarInfo>(atk(m,"desigilname")));
            }
        }

        public void sublongname(Cursor m) {
            if (istrue(atk(m,"sigterm"))) {
                sorry(m, "Sigterm sublongnames NYI");
                make(m, default(VarInfo));
            } else {
                make(m, ast<VarInfo>(atk(m,"subshortname")));
            }
        }

        public void desigilname(Cursor m) {
            if (istrue(atk(m,"variable"))) {
                var r = default(VarInfo);
                r.ind = do_variable_reference(m, ast<VarInfo>(atk(m,"variable")));
                make(m,r);
                check_variable(atk(m,"variable"));
            } else {
                make(m,process_name(atk(m,"longname"),DEFER));
            }
        }

        public void quote__22_20(Cursor m) { make(m,ast<Op.Op>(atk(m,"nibble"))); }
        public void quote__27_27(Cursor m) { make(m,ast<Op.Op>(atk(m,"nibble"))); }
        public void quote__qq(Cursor m) { make(m,ast<Op.Op>(atk(m,"quibble")));}
        public void quote__q(Cursor m) { make(m,ast<Op.Op>(atk(m,"quibble")));}
        public void quote__Q(Cursor m) { make(m,ast<Op.Op>(atk(m,"quibble")));}
        public void quote__s(Cursor m) { make(m,ast<Op.Op>(atk(m,"pat")));}

        public void quote__2f_2f(Cursor m) { make(m,op_for_regex(m,ast<RxOp.RxOp>(atk(m,"nibble")))); }
        public void quote__rx(Cursor m) {
            extract_rx_adverbs(false, false, atk(m,"quibble"));
            make(m,op_for_regex(m,ast<RxOp.RxOp>(atk(m,"quibble"))));
        }

        public void quote__m(Cursor m) {
            make(m,new Op.CallMethod(m, "match", Op.Helpers.mklex(m,"$/"),
                false, Utils.PrependArr(
                    extract_rx_adverbs(true,false,atk(m,"quibble")),
                    op_for_regex(m, ast<RxOp.RxOp>(atk(m,"quibble"))))));
        }

        public void quote__ms(Cursor m) { quote__m(m); }

        internal Op.Op rxembed(Cursor m, Op.Op ops) {
            return inliney_call(m, thunk_sub(ops, new[] { "$¢" }),
                new Op.MakeCursor(m));
        }

        internal Op.Op op_for_regex(Cursor m, RxOp.RxOp rxop) {
            var lift = new List<Op.Op>();
            rxop.oplift(lift);
            var ltm = RxOp.RxOp.optimize_lad(rxop.lad());
            rxop = rxop.rxsimp(false);
            var ops = new Op.RegexBody(m, "", rxop, lift.ToArray());
            var sub = thunk_sub(ops, new [] { "self" }, null, Kernel.RegexMO,
                    Niecza.CLRBackend.DowncallReceiver.BuildLad(ltm));
            addlex(m, sub, "$/", new LISimple(0, null));
            return block_expr(m, sub);
        }

        internal RxOp.RxOp encapsulate_regex(Cursor m, RxOp.RxOp rxop,
                    bool passcut = false) {
            var lift = new List<Op.Op>();
            rxop.oplift(lift);
            var lad = rxop.lad();
            var nrxop = rxop.rxsimp(false);
            var subbody = new Op.RegexBody(m, "", nrxop, lift.ToArray(),
                    passcut);
            var sub = thunk_sub(subbody, new [] { "self" }, null,
                Kernel.RegexMO);
            var subop = new Op.CallSub(m,block_expr(m,sub),true,
                new [] { new Op.MakeCursor(m) });
            return new RxOp.Subrule(subop, lad);
        }

        public void regex_block(Cursor m) {
            if (istrue(atk(m,"quotepair")))
                sorry(m, "Regex adverbs NYI");
            if (istrue(atk(m,"onlystar")))
                make(m,new RxOp.ProtoRedis());
            else
                make(m,ast<RxOp.RxOp>(atk(m,"nibble")));
        }

        public void regex_def_1(Cursor m) {
            throw new NotImplementedException(); // SSTO install_sub
        }

        public void regex_def_2(Cursor m) {
            if (flist(atk(m,"signature")).Length > 1)
                sorry(m, "Too many signatures on regex");

            foreach (var tx in flist(atk(m, "trait"))) {
                var t = ast<Prod<string,object>>(tx);
                if (t.v1 == "unary" || t.v1 == "binary" || t.v1 == "defequiv" ||
                        t.v1 == "of") {
                    // Ignored for now
                } else if (t.v1 == "endsym") {
                    assign(atk(job.rxinfo,"endsym"), mkstr((string)t.v2));
                } else {
                    sorry(m, "Unhandled regex trait {0}", t.v1);
                }
            }

            string cn = (string)job.curlex.GetExtend0("cleanname");
            if ((string)job.curlex.GetExtend0("multi") == "proto") {
                if (cn != null) job.proto_endsym[cn] = asstr(atk(job.rxinfo,"endsym"));
            } else {
                if (cn != null && !isdef(atk(job.rxinfo,"endsym")))
                    assign(atk(job.rxinfo,"endsym"),mkstr(
                        job.proto_endsym.GetDefault(cn, null)));
            }

            assign(atk(job.rxinfo,"dba"), mkstr((string)job.curlex.GetExtend0("name") ?? "anonymous regex"));
        }

        public void regex_def(Cursor m) {
            var ast = ast<RxOp.RxOp>(atk(m,"regex_block"));

            var lift = new List<Op.Op>();
            ast.oplift(lift);
            var ltm = RxOp.RxOp.optimize_lad(ast.lad());

            job.curlex.ltm = Niecza.CLRBackend.DowncallReceiver.BuildLad(ltm);
            ast = ast.rxsimp(false);
            if (ast is RxOp.ProtoRedis) {
                finish_dispatcher(job.curlex, "regex");
            } else {
                finish(job.curlex, new Op.RegexBody(m, (string)(job.curlex.GetExtend0("name") ?? ""), ast, lift.ToArray()));
            }
            // SSTO: reexamine match pruning
            make(m, Op.Helpers.mklex(m, job.curlex.outervar));
        }

        public void regex_declarator__regex(Cursor m) {
            make(m,ast<Op.Op>(atk(m,"regex_def")));
        }
        public void regex_declarator__rule(Cursor m) { regex_declarator__regex(m); }
        public void regex_declarator__token(Cursor m) { regex_declarator__regex(m); }

        public void atom(Cursor m) {
            if (istrue(atk(m,"metachar"))) {
                make(m,ast<RxOp.RxOp>(atk(m,"metachar")));
            } else {
                make(m,new RxOp.String(asstr(m), istrue(atk(job.rxinfo,"i"))));
            }
        }

        struct QuantInfo {
            public Op.Op closure;
            public RxOp.RxOp sep, tilde, tilde_inner;
            public string mod;
            public int min, max;
            public bool minimal, nonlisty, opsep, space, general;
        }

        public void quantified_atom(Cursor m) {
            var atom = ast<RxOp.RxOp>(atk(m,"atom"));
            var q    = istrue(atk(m,"quantifier")) ? ast<QuantInfo>(atk(m,"quantifier")) : default(QuantInfo);

            if (istrue(atk(job.rxinfo, "r"))) {
                // quantifier without explicit :? / :! gets :
                if (q.mod == null) q.mod = "";
            }

            var ss = istrue(atk(job.rxinfo,"s"));

            if (q.max > 0 || q.general) {
                var z = new List<RxOp.RxOp> { atom };
                if (istrue(atk(m,"separator"))) {
                    if (q.sep != null)
                        sorry(m, "Cannot use two separators in one quantifier_atom");
                    var q2 = ast<QuantInfo>(atk(m,"separator"));
                    q.opsep = q2.opsep;
                    q.sep   = q2.sep;
                    q.space = q2.space;
                }
                if (q.sep != null) z.Add(q.sep);
                // parsing quirk. x #`(1) ** #`(21) y, the 1* position is
                // counted as $<normspace> but the 2* is parsed by the
                // quantifier
                if ((q.general || q.sep != null) && ss &&
                        (q.space || istrue(atk(m,"normspace")))) {
                    if (q.sep != null) {
                        z[1] = new RxOp.Sequence(new [] {
                            new RxOp.Sigspace(), z[1], new RxOp.Sigspace() });
                    } else {
                        z.Add(new RxOp.Sigspace());
                    }
                }
                atom = new RxOp.Quantifier(z.ToArray(), q.min, q.max,
                    q.closure, q.mod == "?", q.nonlisty, q.opsep);
            }

            if (q.mod == "")
                atom = new RxOp.Cut(atom);

            if (q.tilde != null) {
                var closer = q.tilde;
                if (closer is RxOp.Cut)
                    closer = closer.zyg[0];
                if (!(closer is RxOp.String)) {
                    sorry(m,"Non-literal closers for ~ NYI");
                    closer = new RxOp.String("",false);
                }
                atom = new RxOp.Sequence(new [] { atom,
                    new RxOp.Tilde(q.tilde_inner, ((RxOp.String)closer).text,
                        asstr(atk(job.rxinfo, "dba"))) });
            }

            make(m,atom);
        }

        public void quantifier__2a(Cursor m) {
            make(m,new QuantInfo { min = 0, max = int.MaxValue, mod = ast<string>(atk(m,"quantmod")) });
        }
        public void quantifier__2b(Cursor m) {
            make(m,new QuantInfo { min = 1, max = int.MaxValue, mod = ast<string>(atk(m,"quantmod")) });
        }
        public void quantifier__3f(Cursor m) {
            make(m,new QuantInfo { min = 0, max = 1, mod = ast<string>(atk(m,"quantmod")) });
        }
        public void quantifier__3a(Cursor m) {
            make(m,new QuantInfo { mod = "" });
        }
        public void quantifier__7e(Cursor m) {
            var tl = flist(atk(m,"quantified_atom"));
            make(m, new QuantInfo { tilde = ast<RxOp.RxOp>(tl[0]),
                tilde_inner = ast<RxOp.RxOp>(tl[1]) });
        }
        public void quantifier__2a2a(Cursor m) {
            var r = new QuantInfo();
            Variable v;
            if (istrue(v = atk(m,"embeddedblock")))
                r = new QuantInfo { min=0, max=int.MaxValue, closure = rxblock(m, v) };
            else if (istrue(v = atk(m, "quantified_atom")))
                r = new QuantInfo { min=1, max=int.MaxValue, sep = ast<RxOp.RxOp>(v) };
            else {
                r = new QuantInfo();
                r.min = (int)Utils.S2N(asstr(atk(m,"0")));
                r.max = istrue(atk(m,"1")) ? (int)Utils.S2N(asstr(atk(m,"1"))) :
                    asstr(m).IndexOf("..") >= 0 ? int.MaxValue : r.min;
            }
            r.mod = ast<string>(atk(m,"quantmod"));
            r.general = true;
            r.space = istrue(atk(m,"normspace"));
            make(m,r);
        }

        public void separator(Cursor m) {
            make(m, new QuantInfo { sep = ast<RxOp.RxOp>(atk(m,"quantified_atom")),
                space = istrue(atk(m,"normspace")),
                opsep = m.global.orig_s[m.from] == '%' });
        }

        public void quantmod(Cursor m) {
            var t = asstr(m);
            if (t == "") { make(m,null); return; }
            if (t[0] == ':') t = t.Substring(1);
            if (t == "+") t = "";
            make(m,t);
        }

        public void quant_atom_list(Cursor m) {
            make(m, new RxOp.Sequence(map(v => ast<RxOp.RxOp>(v),
                flist(atk(m,"quantified_atom")))));
        }

        void LISTrx(Cursor m) {
            var tag = asstr(atk(atk(atk(m,"delims"),"0"),"sym"));
            var zyg = map(z => ast<RxOp.RxOp>(z), flist(atk(m,"list")));
            var dba = asstr(atk(job.rxinfo,"dba"));
            if (tag == "&" || tag == "&") make(m,new RxOp.Conj(zyg));
            if (tag == "||") make(m,new RxOp.SeqAlt(zyg,dba));
            if (tag == "|") make(m,new RxOp.Alt(zyg,dba));
        }

        public void metachar__sigwhite(Cursor m) {
            make(m, istrue(atk(job.rxinfo,"s")) ? (RxOp.RxOp)new RxOp.Sigspace() : rnoop);
        }

        public void metachar__unsp(Cursor m) { make(m, rnoop); }

        public void metachar__7b_7d(Cursor m) {
            make(m, new RxOp.VoidBlock(rxblock(m, atk(m,"embeddedblock"))));
        }

        public Op.Op rxblock(Cursor m, Variable blk) {
            var si = ast<SubInfo>(blk);
            trymop(m, () => {
                addlex(m, si, "$¢", new LISimple(LISimple.NOINIT, null));
                si.sig = new Signature(new [] { Parameter.TPos("$¢",
                    si.dylex["$¢"].SigIndex()) });
            });

            return inliney_call(m, si, new Op.MakeCursor(m));
        }

        public void metachar__mod(Cursor m) {
            // most of these have only parse-time effects
            make(m,ast<RxOp.RxOp>(atk(m,"mod_internal")));
        }

        public void metachar__3a3a(Cursor m) { make(m,new RxOp.CutLTM()); } //::
        public void metachar__3a3a3e(Cursor m) { make(m, new RxOp.CutBrack()); } // ::>
        public void metachar__3a3a3a(Cursor m) { make(m, new RxOp.CutRule()); } // :::

        public void metachar__5b_5d(Cursor m) { // [ ]
            make(m,new RxOp.ConfineLang(ast<RxOp.RxOp>(atk(m,"nibbler"))));
        }

        public void metachar__28_29(Cursor m) { // ( )
            var pnum = asstr(Builtins.postinc(atk(job.rxinfo,"paren")));
            make(m, rxcapturize(m, pnum, encapsulate_regex(m,
                ast<RxOp.RxOp>(atk(m,"nibbler")), true)));
        }

        public void metachar__3c28(Cursor m) { // <(
            make(m, new RxOp.Endpoint("from"));
        }

        public void metachar__293e(Cursor m) { // )>
            make(m, new RxOp.Endpoint("to"));
        }

        public void metachar__3c3c (Cursor m) { // <<
            make(m, new RxOp.ZeroWidth(RxFrame.BEFORE_WORD));
        }
        public void metachar__3e3e (Cursor m) { // >>
            make(m, new RxOp.ZeroWidth(RxFrame.AFTER_WORD));
        }
        public void metachar__ab (Cursor m) { // «
            make(m, new RxOp.ZeroWidth(RxFrame.BEFORE_WORD));
        }
        public void metachar__bb (Cursor m) { // »
            make(m, new RxOp.ZeroWidth(RxFrame.AFTER_WORD));
        }

        public void metachar__7b2a7d(Cursor m) { // {*}
            make(m, new RxOp.ProtoRedis());
        }

        public void metachar__qw(Cursor m) {
            var strings = new List<RxOp.RxOp>();
            trymop(m, () => {
                var words = eval_ast(m, ast<Op.Op>(atk(m,"circumfix")));
                var igcase = istrue(atk(job.rxinfo,"i"));
                foreach (var w in Builtins.UnboxLoS(words))
                    strings.Add(new RxOp.String(w, igcase));
            });
            make(m, new RxOp.Alt(strings.ToArray(), asstr(atk(job.rxinfo,"dba"))));
        }

        // forward...
        RxOp.RxOp rxcapturize(Cursor m, string nm, RxOp.RxOp inp) { throw new NotImplementedException(); }
        Op.Op[] extract_rx_adverbs(bool a, bool b, Variable c) { throw new NotImplementedException(); }
        Op.Op do_variable_reference(Cursor m, VarInfo ast) { throw new NotImplementedException(); }
        void check_variable(Variable v) { throw new NotImplementedException(); }
        string get_cp_ext(Variable c) { throw new NotImplementedException(); }
        Variable eval_ast(Cursor c, Op.Op o) { throw new NotImplementedException(); }

        Op.Op block_expr(Cursor m, SubInfo blk) {
            var name = job.gensym();
            addlex(m, job.curlex, name, new LISub(blk));
            return Op.Helpers.mklex(m, name);
        }

        Op.Op inliney_call(Cursor m, SubInfo block, params Op.Op[] parms) {
            var sym = job.gensym();
            addlex(m, job.curlex, sym, new LISub(block));
            return CompUtils.BetaCall(m, sym, parms);
        }

        internal SubInfo thunk_sub(Op.Op code, string[] parms,
                string name = null, STable cls = null, LAD ltm = null,
                bool nosimpl = false) {

            var n = create_sub(name ?? "ANON", job.curlex, Kernel.BlockMO,
                job.curlex.cur_pkg, job.curlex.in_class, false, null);

            n.special |= SubInfo.TRANSPARENT;
            n.ltm = ltm;
            Parameter[] ps = new Parameter[parms.Length];
            for (int i = 0; i < ps.Length; i++) {
                addlex(null, n, parms[i], new LISimple(LISimple.NOINIT, null));
                ps[i] = Parameter.TPos(parms[i], n.dylex[parms[i]].SigIndex());
            }
            n.sig = new Signature(ps);
            finish(n, code, nosimpl);
            return n;
        }

        // does NOT handle $?PERL construction
        internal SubInfo create_sub(string name, SubInfo outer, STable cls,
                STable pkg, STable icl, bool once, Frame outer_frame) {

            var n = new SubInfo(job.unit, name, outer, cls, pkg, once, outer_frame);
            n.in_class = icl;
            if (n.outer != null && n.outer.unit == job.unit)
                n.outer.children.Add(n);
            job.unit.our_subs.Add(n);
            return n;
        }

        internal void finish(SubInfo n, Op.Op code, bool done = false) {
            if (!done) code = code.simplify(n);
            if (code.onlystub()) n.SetExtend("onlystub", true);
            // serialize cgops
            n.nam_str = code.cgop(n).ToString(out n.nam_refs);
            n.code = RuntimeUnit.JitCompileSub;
            if (n.protopad != null)
                n.protopad.code = n.code;
        }

        internal void finish_dispatcher(SubInfo s, string k) {
            if (k == "regex")
                s.code = Lexer.StandardProtoC;
            else if (k == "multi")
                s.code = Kernel.StandardTypeProtoC;
            else
                throw new Exception("Unknown dispatcher type " + k);
            if (s.protopad != null)
                s.protopad.code = s.code;
        }
    }
}
