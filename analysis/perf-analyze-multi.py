#!/usr/bin/env python3
"""Per-knob render-decomposition analysis (CSV with a 'knob' column).
Each knob was toggled on(A)/off(B) in paired ABBA windows within ONE session.
Reports, per knob: median(on), median(off), delta = cost of that knob (CPU+GPU),
Mann-Whitney p, bootstrap 95% CI. Same stats as perf-analyze.py. Pure stdlib.
Usage: perf-analyze-multi.py <csv> [--boot 20000] [--seed 1]
"""
import sys, csv, math, random, statistics as st
from collections import defaultdict

def median(xs): return st.median(xs) if xs else float("nan")

def mw_p(a, b):
    na, nb = len(a), len(b)
    if na == 0 or nb == 0: return float("nan")
    comb = sorted([(v,0) for v in a] + [(v,1) for v in b])
    ranks=[0.0]*len(comb); i=0; n=len(comb); tie=0.0
    while i < n:
        j=i
        while j+1 < n and comb[j+1][0]==comb[i][0]: j+=1
        r=(i+1+j+1)/2.0
        for k in range(i,j+1): ranks[k]=r
        t=j-i+1
        if t>1: tie+=t**3-t
        i=j+1
    Ra=sum(r for r,(_,g) in zip(ranks,comb) if g==0)
    Ua=Ra-na*(na+1)/2.0; U=min(Ua, na*nb-Ua); mu=na*nb/2.0; N=na+nb
    sd=math.sqrt((na*nb/12.0)*((N+1)-tie/(N*(N-1))))
    if sd==0: return 1.0
    z=(U-mu)/sd
    return 2.0*(1.0-0.5*(1.0+math.erf(abs(z)/math.sqrt(2))))

def boot(a, b, R, seed):
    rng=random.Random(seed); na,nb=len(a),len(b); d=[]
    for _ in range(R):
        ra=[a[rng.randrange(na)] for _ in range(na)]
        rb=[b[rng.randrange(nb)] for _ in range(nb)]
        d.append(median(ra)-median(rb))
    d.sort(); return d[int(0.025*R)], d[int(0.975*R)]

if __name__=="__main__":
    args=sys.argv[1:]; R=20000; SEED=1; path=None; i=0
    while i < len(args):
        if args[i]=="--boot": R=int(args[i+1]); i+=2
        elif args[i]=="--seed": SEED=int(args[i+1]); i+=2
        else: path=args[i]; i+=1
    if not path: print("usage: perf-analyze-multi.py <csv>"); sys.exit(2)
    raw=open(path,encoding="utf-8",errors="replace").read().strip()
    if raw.startswith("ERROR"): print("harness error:", raw); sys.exit(1)
    rows=list(csv.DictReader(raw.splitlines()))
    # knob -> window -> cond + sample lists
    cpu=defaultdict(lambda: defaultdict(list)); gpu=defaultdict(lambda: defaultdict(list)); cond=defaultdict(dict)
    for r in rows:
        if r.get("drop")=="1": continue
        k=r["knob"]; w=int(r["window"]); cond[k][w]=r["cond"]
        c=float(r["cpu_ms"]); g=float(r["gpu_ms"])
        if c>=0: cpu[k][w].append(c)
        if g>=0: gpu[k][w].append(g)
    print(f"file: {path}   bootstrap R={R}\n")
    results=[]
    for k in cpu:
        def meds(src):
            A=[median(src[k][w]) for w in src[k] if cond[k][w]=="A"]
            B=[median(src[k][w]) for w in src[k] if cond[k][w]=="B"]
            return A,B
        cA,cB=meds(cpu); gA,gB=meds(gpu)
        dc=median(cA)-median(cB); dg=median(gA)-median(gB)
        clo,chi=boot(cA,cB,R,SEED); glo,ghi=boot(gA,gB,R,SEED)
        results.append((k,dc,clo,chi,mw_p(cA,cB),dg,glo,ghi,mw_p(gA,gB),len(cA),len(cB)))
    results.sort(key=lambda x:-x[1])
    print(f"{'knob':12} {'CPU dms':>8} {'CPU 95% CI':>18} {'p':>9}   {'GPU dms':>8} {'GPU 95% CI':>18} {'p':>9}")
    for k,dc,clo,chi,cp,dg,glo,ghi,gp,nA,nB in results:
        sig='*' if (clo>0 or chi<0) else ' '
        print(f"{k:12} {dc:8.3f} [{clo:7.3f},{chi:7.3f}]{sig} {cp:9.1e}   {dg:8.3f} [{glo:7.3f},{ghi:7.3f}] {gp:9.1e}")
    print("\ndelta = median(on) - median(off) = marginal cost of that knob at current settings (ms/frame).")
    print("negative = disabling it COSTS more (i.e. the knob is a net win, e.g. SRP batcher).  * = CPU CI excludes 0.")
