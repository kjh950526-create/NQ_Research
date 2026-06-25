# 분봉(15분) Zone 추세추종 연구 — Plan B

스캘핑(루트 daily_merged/)과 **완전 분리**된 분봉 연구 폴더.

## 워크플로우
- 거래자: 매 세션 9:30(ET) 전, 전날 세션 응집구간이 과거 며칠과 일치하는지 보고 zone 식별 → 구글시트(bar_zone_log)에 4칸 기록(session_date, zone_range, lookback_refs, note). 그날 *새로 찾은* zone만. 당일 차트 실시간 관찰 불필요(zone=과거로 정의, 반응=마감후).
- Claude: 시트 받아서 → zone_id 부여·zone_master 관리 → 1분봉 raw(trend/)로 반응·월장·유지·카오스·퀄리티 측정 → zone_daily_reaction에 기록. zone 수명(생성~사망) zone_id로 추적.

## 파일
- `zone_master.csv`: zone_id, 생성일, 범위, 활성여부, 사망일 (Claude 관리)
- `zone_lookback_refs.csv`: zone_id별 과거 참조점 (Claude 관리)
- `zone_daily_reaction.csv`: 일별 zone 반응, realtime_catchable만 거래자, 나머지 Claude raw 측정
- `zone_logs/`: 거래자 시트 스냅샷 보관용

## 데이터
- 1분봉: `../trend/mnq_1m_morning_0930_1200_ET.csv` (또는 full)
- 검증: 15분봉 리샘플해서 zone 반응 측정

## 방법론
RESEARCH_LOG §10 참조 (특히 §10.9 propfirm 당일청산 제약, §10.10 워크플로우)
