# ChaosMeter.cs 로직을 Python으로 복제해 pandas 방식과 대조하는 검증 스크립트
# 인디케이터 수정 시 반드시 재실행해 30/30 일치 확인
import pandas as pd, numpy as np, glob, math
def lr(d):
    fs=glob.glob(f'raw/*{d}*.csv')
    if not fs: return None
    x=pd.read_csv(fs[0]); x['t']=pd.to_datetime(x['time'],errors='coerce')
    for c in ['high','low','close']: x[c]=pd.to_numeric(x[c],errors='coerce')
    return x.dropna(subset=['t','close']).sort_values('t')
def csharp_one(x,day,s,e,off):
    ds=pd.Timestamp(f"{day} {s}"); de=pd.Timestamp(f"{day} {e}")
    win=x[(x['t']>ds)&(x['t']<=de)]
    if len(win)<30: return -1
    origin=ds+pd.Timedelta(seconds=off); bars={}
    for _,tk in win.iterrows():
        binidx=math.ceil((tk['t']-origin).total_seconds()/10.0)
        binend=origin+pd.Timedelta(seconds=binidx*10)
        if binend not in bars: bars[binend]=[tk['high'],tk['low'],tk['close'],tk['t'].value]
        else:
            b=bars[binend]
            if tk['high']>b[0]:b[0]=tk['high']
            if tk['low']<b[1]:b[1]=tk['low']
            if tk['t'].value>=b[3]:b[2]=tk['close'];b[3]=tk['t'].value
    if len(bars)<6: return -1
    hi=max(b[0] for b in bars.values()); lo=min(b[1] for b in bars.values()); mid=(hi+lo)/2
    cnt=0;prev=0
    for k in sorted(bars.keys()):
        c=bars[k][2]; side=1 if c>mid else(-1 if c<mid else prev)
        if side==0: continue
        if prev!=0 and side!=prev: cnt+=1
        prev=side
    return cnt
def csharp_ens(x,day,s,e):
    ms=[csharp_one(x,day,s,e,o) for o in range(10)]; ms=[m for m in ms if m>=0]
    return np.mean(ms) if len(ms)==10 else np.nan
if __name__=='__main__':
    for f in sorted(glob.glob('raw/raw_MNQ_SEP26_*.csv')):
        d=f.split('_')[-1][2:8]; x=lr(d)
        if x is None: continue
        day=x['t'].iloc[0].strftime('%Y-%m-%d')
        print(f"{d}: {csharp_ens(x,day,'10:00:00','10:10:00'):.1f}")
