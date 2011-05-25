class NieczaCompiler;

use JSYNC;

has $.module_finder;
has $.backend;
has $.stages;
has $.frontend;
has $.verbose;
has $!main-sn = 0;

has $.unitcache = {};

sub gettimeofday() { now.to-posix.[0] }

method !compile($unitname, $filename, $modtime, $source, $main, $run, $end, $evalmode) {
    my %*units;

    my $*module_loader = sub ($m) { self!load_dependent($m) };
    my $*compiler = self;
    my $*verbose = $.verbose;

    my $ast;
    my @steps = (
        $.frontend.typename => { $ast = $.frontend.parse(:$unitname,
            :$filename, :$modtime, :$source); },
        (map -> $st { $st.typename => { $ast = $st.invoke($ast) } }, @$.stages),
        $.backend.typename => { $.backend.accept($unitname, $ast, :$main, :$run, :$evalmode) },
    );

    for @steps -> $step {
        my $start = gettimeofday;
        $step.value.();
        my $time = gettimeofday() - $start;

        if $.verbose {
            say "$unitname: $step.key() took $time";
        }

        if $end eq $step.key {
            say to-jsync($ast);
            last;
        }
    }
}

method compile_module($module, $stop = "") {
    my ($filename, $modtime, $source) = $.module_finder.load_module($module);
    self!compile($module, $filename, $modtime, $source, False, False, $stop, False);
}

method !main_name() {
    my $i = $!main-sn++;
    $i ?? "MAIN_$i" !! "MAIN";
}

method compile_file($file, $run, $stop = "") {
    my ($filename, $modtime, $source) = $.module_finder.load_file($file);
    self!compile(self!main_name, $filename, $modtime, $source, True, $run, $stop, False);
}

method compile_string($source, $run, $stop = "", :$evalmode = False) {
    self!compile(self!main_name, "(eval)", 0, $source, True, $run, $stop, $evalmode);
}

method !up_to_date($mod) {
    say "Checking datedness of $mod.name()" if $.verbose;
    for $mod.tdeps.pairs -> $p {
        my ($filename, $modtime, $source) = $.module_finder.load_module($p.key);
        if $filename ne $p.value.[0] {
            say "$p.key() resolves to $filename now, was $p.value.[0]" if $.verbose;
            return False;
        }
        # number storage isn't reliable atm and frequently causes small
        # errors, especially on Windows
        if $modtime - $p.value.[1] > 0.001 {
            say "$p.key() mod-time increased from $p.value.[1] to $modtime" if $.verbose;
            return False;
        }
    }
    return True;
}

method !load_dependent($module) {
    say "Trying to load depended module $module" if $.verbose;
    my $newmod = $.unitcache{$module} //= $.backend.get_unit($module);

    if !defined($newmod) || !self!up_to_date($newmod) {
        $.unitcache{$module}:delete;
        say "(Re)compilation needed" if $.verbose;
        note "[auto-compiling setting]" if $module eq 'CORE';
        self.compile_module($module);
        note "[done]" if $module eq 'CORE';
        $newmod = $.unitcache{$module} = $.backend.get_unit($module);
    }

    %*units{$module} = $newmod;
    for keys $newmod.tdeps -> $mn {
        %*units{$mn} //= ($.unitcache{$mn} //= $.backend.get_unit($mn));
    }
    say "Loaded $module" if $.verbose;
    $newmod;
}
