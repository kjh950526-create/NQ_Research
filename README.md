# NQ_Research

NQ/MNQ 마이크로 선물 인트라데이 재량 스캘핑 전략의 엣지 검증 연구.
거래자: Jeonghun (서울, 클래식 피아니스트). 프롭펌 MFFU.

## 핵심 문서 (여기부터 읽어라)
- **`docs/RESEARCH_LOG.md`** — 마스터 연구 기록. 맨 위 §0 강제 프로토콜 + §0.6 계산 규칙 필독.
  새 세션은 이 문서 §0을 먼저 읽고 시작한다.

## 폴더 구조
```
Journal_v3 - journal.csv   시트(구글드라이브 백업, 본체/시간외 거래# 태그) - 루트 정위치
chaos_meter_log.csv        인디케이터 자동 출력 미터 로그 - 루트 정위치
README.md

docs/          RESEARCH_LOG.md (마스터 기록, backbone)
daily_merged/  merged_log.csv (PD+판단 통합 마스터 데이터), 미터 데이터
pd/            일별 거래기록 (YYMMDD_pd.csv, NinjaTrader)
raw/           일별 1초봉 (raw_MNQ_[계약]_[YYYYMMDD].csv)
traces/        NinjaTrader 시스템 로그 (trace.YYYYMMDD.*.txt)
indicators/    ChaosMeter.cs (카오스미터 인디, 최신본), verify
scripts/       분석 파이썬 스크립트 (zones2, daily_checklist, 슬립/트레이스 파서 등)
research_data/  참고 데이터 (legacy 1초봉 235일, trend, bar_research, legacy)
archive/       zip 백업, 인디 구버전
journal/       decision_journal_light.xlsx (시트 백업본)
```

## 현재 단계 (2026-08)
미터 규칙(≤4 정방향/5+ 역방향) 하의 승률 60.9% 엣지를 **라이브에서 검증** 중.
- 계좌 2개: mnq1(검증, 시간외 함) + mnq3(통과, 10:30 copier OFF).
- 8월 = 전략 고정, 관찰만. 9월 구독갱신 = 통과 본격화.
- 상세 맥락·모든 결정 근거는 `docs/RESEARCH_LOG.md` 참조.

## 데이터 규칙 (자주 틀리는 것 — RESEARCH_LOG §0.6)
- 승률은 **미터 규칙 적용**(역방향날 -R 뒤집기) = 60.9%. raw R>0(47%)는 미가공값, 쓰지 말것.
- 시간외 분류는 **시트 거래# 컬럼** 기준(basis 텍스트 아님).
- 커미션/틱값 **절대 추정 금지** (MNQ왕복 $1.90=0.95pt, 손익분기 52.4%).
