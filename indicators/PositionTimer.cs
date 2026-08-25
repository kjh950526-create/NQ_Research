// PositionTimer.cs
// 진입 후 경과 시간 타이머 + 실계좌 포지션 연동
//
// 목적(§6.153): 60초가 의미있는 판단 시점으로 확인됨.
//   - 절반이 60초 안에 결판, 생존한 것 중 60초 상태가 예후를 가름.
//   - 진입 후 "몇 초 경과"를 실시간으로 알려 재량/기계적 판단 보조.
//
// 기능:
//   - 실계좌 포지션 감시: Flat→Long/Short = 진입감지(타이머 시작),
//     Long/Short→Flat = 청산감지(타이머 리셋).
//   - 패널에 경과 초 표시.
//   - 60초 도달 시 노랑, 90초 도달 시 주황으로 색 변화.
//
// 주의: 인디케이터가 감시할 계좌를 AccountName 설정으로 지정.
//   비워두면 첫 번째 non-simulation 계좌, 없으면 첫 계좌 사용.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;

namespace NinjaTrader.NinjaScript.Indicators
{
	public class PositionTimer : Indicator
	{
		// ── 상태
		private Account   watchedAccount   = null;
		private bool      subscribed       = false;
		private MarketPosition curPos      = MarketPosition.Flat;
		private DateTime  entryTime        = DateTime.MinValue;  // 진입 시각(실시간 기준)
		private bool      inPosition       = false;

		// 렌더 캐시 (OnRender는 UI스레드, 상태변경은 다른스레드일수있어 값만 복사)
		private volatile int    elapsedSec = 0;
		private volatile string posText    = "FLAT";

		// ★ 차트시간 추적 (Playback 대응: DateTime.Now 대신 데이터 시각 사용)
		//   라이브에선 데이터시각≈실제시각, 재생에선 재생시각을 따라감 → 둘다 정확.
		private DateTime lastDataTime  = DateTime.MinValue;  // 최신 틱의 시각
		private DateTime entryDataTime = DateTime.MinValue;  // 진입 순간의 데이터 시각

		// ★ 어느 계좌를 감시중인지 화면표시용 (여러 계좌 동시운용 대비)
		private volatile string acctLabel = "?";

		// ★ 신호등(§6.153): 60초 시점 net<-5면 빨강, 아니면 초록, 60초전 회색.
		//   한번 정해지면(60초 통과) 청산까지 고정.
		private double entryPrice   = 0.0;         // 진입가
		private int    posSign      = 0;           // +1 롱, -1 숏
		private volatile int lightState = 0;       // 0=회색(판단전) 1=초록 2=빨강
		private bool   lightLocked  = false;       // 60초 지나 신호등 확정됨

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "진입 후 경과시간 타이머 (실계좌 포지션 연동, 60/90초 색변화)";
				Name        = "PositionTimer";
				Calculate   = Calculate.OnEachTick;
				IsOverlay   = true;
				DisplayInDataBox        = false;
				PaintPriceMarkers       = false;
				IsSuspendedWhileInactive = false;

				AccountName  = "";     // 비우면 자동선택
				PanelCorner  = 1;      // 0=좌상 1=우상 2=좌하 3=우하
				FontSizePx   = 22;
				SignalSec    = 60;     // 신호등 확정 시점(초)
				BadThreshold = 5;      // 이 시점 net < -이값 이면 빨강 (기본 -5pt)
			}
			else if (State == State.Configure)
			{
				// 1초 시리즈: 타이머를 1초마다 갱신하기 위함
				AddDataSeries(BarsPeriodType.Second, 1);
			}
			else if (State == State.DataLoaded)
			{
				ResolveAccount();
			}
			else if (State == State.Terminated)
			{
				Unsubscribe();
			}
		}

		// ── 감시할 계좌 결정
		//   우선순위: (1)AccountName 지정 → (2)차트트레이더 선택계좌 → (3)폴백
		private void ResolveAccount()
		{
			lock (Account.All)
			{
				// 1) 이름 지정 시 정확히 그 계좌 (수동 오버라이드)
				if (!string.IsNullOrEmpty(AccountName))
				{
					watchedAccount = Account.All.FirstOrDefault(a => a.Name == AccountName);
					if (watchedAccount == null)
					{
						acctLabel = "!" + AccountName + "?";
						return;
					}
					acctLabel = watchedAccount.Name;
				}
				else
				{
					// 2) ★차트트레이더가 선택한 계좌 따라가기 (차트에 보이는 그 계좌)
					Account ct = TryGetChartTraderAccount();
					if (ct != null)
					{
						watchedAccount = ct;
						acctLabel = "CT:" + ct.Name;
					}
					else
					{
						// 3) 폴백: 이차트 상품 포지션 가진 계좌 → non-Sim 첫계좌
						Account pick = null;
						if (Instrument != null)
						{
							pick = Account.All.FirstOrDefault(a =>
							{
								lock (a.Positions)
									return a.Positions.Any(x => x.Instrument != null
										&& x.Instrument.FullName == Instrument.FullName
										&& x.MarketPosition != MarketPosition.Flat);
							});
						}
						if (pick == null)
							pick = Account.All.FirstOrDefault(a => !a.Name.ToLower().Contains("sim"))
								?? Account.All.FirstOrDefault();
						watchedAccount = pick;
						acctLabel = watchedAccount != null ? "auto:" + watchedAccount.Name : "?";
					}
				}
			}
			Subscribe();
		}

		// ★ 차트트레이더가 현재 선택한 계좌를 가져오기.
		//   NT8 버전에 따라 접근경로가 달라 여러 방법 시도(리플렉션 폴백).
		private Account TryGetChartTraderAccount()
		{
			try
			{
				if (ChartControl == null || ChartControl.OwnerChart == null) return null;
				var owner = ChartControl.OwnerChart;

				// 방법1: OwnerChart.ChartTrader.Account (속성 직접)
				var ctProp = owner.GetType().GetProperty("ChartTrader");
				object ctObj = ctProp != null ? ctProp.GetValue(owner) : null;
				if (ctObj != null)
				{
					var accProp = ctObj.GetType().GetProperty("Account");
					var acc = accProp != null ? accProp.GetValue(ctObj) as Account : null;
					if (acc != null) return acc;
				}

				// 방법2: OwnerChart.ChartTraderControl.Account
				var ctcProp = owner.GetType().GetProperty("ChartTraderControl");
				object ctcObj = ctcProp != null ? ctcProp.GetValue(owner) : null;
				if (ctcObj != null)
				{
					var accProp = ctcObj.GetType().GetProperty("Account");
					var acc = accProp != null ? accProp.GetValue(ctcObj) as Account : null;
					if (acc != null) return acc;
				}
			}
			catch { /* 접근 실패 시 폴백으로 */ }
			return null;
		}

		private void Subscribe()
		{
			if (watchedAccount == null || subscribed) return;
			watchedAccount.PositionUpdate += OnPositionUpdate;
			subscribed = true;
			// 현재 포지션 초기화
			SyncCurrentPosition();
		}

		private void Unsubscribe()
		{
			if (watchedAccount != null && subscribed)
				watchedAccount.PositionUpdate -= OnPositionUpdate;
			subscribed = false;
		}

		// 이미 포지션 보유중인 상태에서 인디케이터를 붙였을 때 대비
		private void SyncCurrentPosition()
		{
			if (watchedAccount == null) return;
			Position p = null;
			lock (watchedAccount.Positions)
			{
				p = watchedAccount.Positions.FirstOrDefault(x =>
					Instrument != null && x.Instrument != null
					&& x.Instrument.FullName == Instrument.FullName);
			}
			if (p != null && p.MarketPosition != MarketPosition.Flat)
			{
				curPos     = p.MarketPosition;
				inPosition = true;
				// 진입시각 정보를 알 수 없으므로 지금부터 카운트(근사)
				entryTime     = DateTime.Now;
				entryDataTime = lastDataTime;   // 데이터 시각 기준
				posText    = curPos == MarketPosition.Long ? "LONG" : "SHORT";
				// ★ 진입가·방향 (이미 보유중이던 포지션)
				entryPrice  = p.AveragePrice;
				posSign     = curPos == MarketPosition.Long ? 1 : -1;
				lightState  = 0;
				lightLocked = false;
			}
			else
			{
				curPos = MarketPosition.Flat; inPosition = false; posText = "FLAT";
			}
		}

		// ── 포지션 변화 이벤트 (진입/청산 감지)
		private void OnPositionUpdate(object sender, PositionEventArgs e)
		{
			if (e.Position == null || e.Position.Instrument == null) return;
			// ★ 이 이벤트가 감시중인 계좌 것인지 확인 (여러 계좌 동시운용 안전)
			if (watchedAccount != null && e.Position.Account != null
				&& e.Position.Account.Name != watchedAccount.Name) return;
			// 이 차트의 상품만
			if (Instrument == null || e.Position.Instrument.FullName != Instrument.FullName) return;

			MarketPosition newPos = e.Position.MarketPosition;

			// Flat → 포지션 = 진입
			if (curPos == MarketPosition.Flat && newPos != MarketPosition.Flat)
			{
				entryTime     = DateTime.Now;
				entryDataTime = lastDataTime;
				inPosition = true;
				posText    = newPos == MarketPosition.Long ? "LONG" : "SHORT";
				// ★ 진입가·방향 기록 + 신호등 초기화
				entryPrice  = e.Position.AveragePrice;
				posSign     = newPos == MarketPosition.Long ? 1 : -1;
				lightState  = 0;      // 회색(판단전)
				lightLocked = false;
			}
			// 포지션 → Flat = 청산 (타이머 리셋)
			else if (curPos != MarketPosition.Flat && newPos == MarketPosition.Flat)
			{
				inPosition = false;
				entryTime     = DateTime.MinValue;
				entryDataTime = DateTime.MinValue;
				elapsedSec = 0;
				posText    = "FLAT";
				// ★ 신호등 리셋
				lightState  = 0;
				lightLocked = false;
				entryPrice  = 0.0;
				posSign     = 0;
			}
			// 방향 전환 (Long→Short 등, 드묾): 진입시각 갱신
			else if (curPos != MarketPosition.Flat && newPos != MarketPosition.Flat && curPos != newPos)
			{
				entryTime     = DateTime.Now;
				entryDataTime = lastDataTime;
				inPosition = true;
				posText    = newPos == MarketPosition.Long ? "LONG" : "SHORT";
				// ★ 새 포지션이므로 진입가·신호등 재설정
				entryPrice  = e.Position.AveragePrice;
				posSign     = newPos == MarketPosition.Long ? 1 : -1;
				lightState  = 0;
				lightLocked = false;
			}

			curPos = newPos;
		}

		protected override void OnBarUpdate()
		{
			// 최신 데이터 시각 추적 (Playback/라이브 공통)
			if (Times != null && Times[BarsInProgress] != null && CurrentBars[BarsInProgress] >= 0)
				lastDataTime = Times[BarsInProgress][0];

			// 계좌 미해결 시 재시도 (연결 지연 대비)
			if (watchedAccount == null)
			{
				ResolveAccount();
			}
			// ★ AccountName 미지정(=차트트레이더 추종) 시, 사용자가 CT 계좌를
			//   바꿨을 수 있으니 주기적으로 재확인. 바뀌었으면 재구독.
			else if (string.IsNullOrEmpty(AccountName) && BarsInProgress == 1)
			{
				Account ct = TryGetChartTraderAccount();
				if (ct != null && ct.Name != watchedAccount.Name)
				{
					Unsubscribe();
					watchedAccount = ct;
					acctLabel = "CT:" + ct.Name;
					// 포지션 상태 초기화 후 새 계좌로 재구독
					curPos = MarketPosition.Flat; inPosition = false;
					entryDataTime = DateTime.MinValue; elapsedSec = 0; posText = "FLAT";
					Subscribe();
				}
			}

			// 경과 시간 갱신: 데이터 시각 기준(재생 대응).
			if (inPosition)
			{
				if (entryDataTime != DateTime.MinValue && lastDataTime != DateTime.MinValue
					&& lastDataTime >= entryDataTime)
					elapsedSec = (int)Math.Max(0, (lastDataTime - entryDataTime).TotalSeconds);
				else if (entryTime != DateTime.MinValue)
					elapsedSec = (int)Math.Max(0, (DateTime.Now - entryTime).TotalSeconds);

				// ★ 신호등: 60초 도달 순간 net 측정해 확정(이후 고정).
				//   net = (현재가 - 진입가) * 방향. net < -Threshold면 빨강, 아니면 초록.
				if (!lightLocked && elapsedSec >= SignalSec && entryPrice > 0 && posSign != 0
					&& BarsInProgress == 1 && CurrentBars[1] >= 0)
				{
					double px  = Closes[1][0];              // 1초봉 현재가
					double net = (px - entryPrice) * posSign;
					lightState  = (net < -BadThreshold) ? 2 : 1;   // 2=빨강 1=초록
					lightLocked = true;
				}
			}
		}

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			base.OnRender(chartControl, chartScale);
			if (chartControl == null) return;

			// 배경색: 시간과 무관하게 중립. (신호는 작은 라이트로 표시)
			SharpDX.Color bg;
			string line1, line2;

			if (!inPosition)
			{
				bg = new SharpDX.Color(70, 70, 70, 200);
				line1 = "TIMER: FLAT";
				line2 = "[" + acctLabel + "]";
			}
			else
			{
				bg = new SharpDX.Color(55, 65, 80, 210);   // 진행중 중립(청회색)
				int s = elapsedSec;
				int mm = s / 60, ss = s % 60;
				line1 = string.Format("{0} {1}:{2:00}", posText, mm, ss);
				line2 = string.Format("{0}s  [{1}]", s, acctLabel);
			}

			var tf = new SharpDX.DirectWrite.TextFormat(
				NinjaTrader.Core.Globals.DirectWriteFactory, "Arial",
				SharpDX.DirectWrite.FontWeight.Bold,
				SharpDX.DirectWrite.FontStyle.Normal, FontSizePx);

			string text = line1 + "\n" + line2;

			float w = 200, h = 62;
			float pad = 10;
			float x, y;
			float left   = (float)ChartPanel.X;
			float top    = (float)ChartPanel.Y;
			float right  = (float)(ChartPanel.X + ChartPanel.W);
			float bottom = (float)(ChartPanel.Y + ChartPanel.H);
			switch (PanelCorner)
			{
				case 1:  x = right - w - pad; y = top + pad; break;
				case 2:  x = left + pad;      y = bottom - h - pad; break;
				case 3:  x = right - w - pad; y = bottom - h - pad; break;
				default: x = left + pad;      y = top + pad; break;
			}

			var rect     = new SharpDX.RectangleF(x, y, w, h);
			var brushBg  = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, bg);
			var brushTx  = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, SharpDX.Color.White);
			RenderTarget.FillRectangle(rect, brushBg);

			var layoutRect = new SharpDX.RectangleF(x + 8, y + 6, w - 12, h - 8);
			RenderTarget.DrawText(text, tf, layoutRect, brushTx);

			// ★ 신호등 라이트 (작은 원): 우측에 표시.
			//   0=회색(60초전/판단전) 1=초록(60초net양호) 2=빨강(60초 net<-5).
			SharpDX.Color lightCol;
			switch (lightState)
			{
				case 1:  lightCol = new SharpDX.Color(40, 200, 80, 255);  break;  // 초록
				case 2:  lightCol = new SharpDX.Color(230, 40, 40, 255);  break;  // 빨강
				default: lightCol = new SharpDX.Color(120, 120, 120, 180); break; // 회색
			}
			float r  = 9f;                          // 반지름
			float cx = x + w - r - 12;              // 패널 우측
			float cy = y + h * 0.5f;
			var lightBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, lightCol);
			var ellipse = new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2(cx, cy), r, r);
			RenderTarget.FillEllipse(ellipse, lightBrush);
			// 테두리(살짝 어둡게)
			var ringBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color(20, 20, 20, 200));
			RenderTarget.DrawEllipse(ellipse, ringBrush, 1.5f);
			lightBrush.Dispose();
			ringBrush.Dispose();

			brushBg.Dispose();
			brushTx.Dispose();
			tf.Dispose();
		}

		#region Properties
		[NinjaScriptProperty]
		[Display(Name = "감시 계좌명(비우면 차트트레이더 추종)", Order = 1, GroupName = "설정")]
		public string AccountName { get; set; }

		[NinjaScriptProperty]
		[Range(0, 3)]
		[Display(Name = "패널 위치(0좌상1우상2좌하3우하)", Order = 2, GroupName = "설정")]
		public int PanelCorner { get; set; }

		[NinjaScriptProperty]
		[Range(8, 60)]
		[Display(Name = "글자 크기(px)", Order = 3, GroupName = "설정")]
		public int FontSizePx { get; set; }

		[NinjaScriptProperty]
		[Range(1, 600)]
		[Display(Name = "신호등 확정 시점(초)", Order = 4, GroupName = "설정")]
		public int SignalSec { get; set; }

		[NinjaScriptProperty]
		[Range(0.0, 50.0)]
		[Display(Name = "빨강 임계(net<-이값 pt)", Order = 5, GroupName = "설정")]
		public double BadThreshold { get; set; }
		#endregion
	}
}
