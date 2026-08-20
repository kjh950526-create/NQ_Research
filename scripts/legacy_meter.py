import pandas as pd, numpy as np, glob, os
def compute_ens(fn):
    try:
        x=pd.read_csv(fn)
        x['t']=pd.to_datetime(x['Time'],errors='coerce')
        for c in ['High','Low','Close']: x[c]=pd.to_numeric(x[c],errors='coerce')
        x=x.dropna(subset=['t','Close']).sort_values('t')
        if len(x)==0: return None
        day=x['t'].iloc[0].strftime('%Y-%m-%d')
        t0=pd.Timestamp(f"{day} 10:00:00")
        w=x[(x['t']>t0)&(x['t']<=t0+pd.Timedelta(minutes=10))]
        if len(w)<30: return None
        vals=[]
        for off in range(10):
            b=w.set_index('t').resample('10s',label='right',closed='right',origin=t0+pd.Timedelta(seconds=off)).agg({'High':'max','Low':'min','Close':'last'}).dropna()
            if len(b)<6: continue
            mid=(b['High'].max()+b['Low'].min())/2; c=0;prev=0
            for v in b['Close']:
                s=1 if v>mid else(-1 if v<mid else prev)
                if s==0:continue
                if prev!=0 and s!=prev:c+=1
                prev=s
            vals.append(c)
        return np.mean(vals) if len(vals)==10 else None
    except Exception as e:
        return None

rows=[]
for folder in ['_u/legacy_hdvo(250616-251107)','_u/legacy_hdvo(251110-260526)']:
    for fn in sorted(glob.glob(f'{folder}/*_hdvo.txt')):
        base=os.path.basename(fn)
        d=base[:6]  # YYMMDD
        m=compute_ens(fn)
        if m is not None:
            date=f"20{d[:2]}-{d[2:4]}-{d[4:6]}"
            rows.append((date,m))
df=pd.DataFrame(rows,columns=['date','meter'])
df['date']=pd.to_datetime(df['date'])
df=df.sort_values('date').reset_index(drop=True)
df.to_csv('/tmp/legacy_meters.csv',index=False)
print(f"총 {len(df)}일 미터 계산 완료")
print(f"기간: {df.date.min().date()} ~ {df.date.max().date()}")
g=(df.meter<=4).sum(); y=((df.meter>4)&(df.meter<7)).sum(); r=(df.meter>=7).sum()
print(f"\n전체: 초록 {g}({g/len(df)*100:.0f}%) 노랑 {y}({y/len(df)*100:.0f}%) 빨강 {r}({r/len(df)*100:.0f}%)")

# 월별 초록 빈도
df['ym']=df.date.dt.to_period('M')
print(f"\n=== 월별 초록 빈도 ===")
for ym,grp in df.groupby('ym'):
    gg=(grp.meter<=4).sum()
    print(f"  {ym}: 초록 {gg}/{len(grp)} = {gg/len(grp)*100:.0f}% (평균미터 {grp.meter.mean():.1f})")
