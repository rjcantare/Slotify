package com.rentalsystem.android.mqtt

object Topics {
    const val ConfigRates = "rental/config/rates"
    const val ConfigTabletApps = "rental/config/tablet-apps"
    const val ConfigTabletAdmin = "rental/config/tablet-admin"
    const val ConfigAbusePrevention = "rental/config/abuse-prevention"
    const val ConfigUpdate = "rental/config/update"

    fun stationStatus(stationId: String) = "rental/station/$stationId/status"
    fun stationSession(stationId: String) = "rental/station/$stationId/session"
    fun stationClaimRequest(stationId: String) = "rental/station/$stationId/claim/request"
    fun stationClaimResult(stationId: String) = "rental/station/$stationId/claim/result"
    fun stationClaimEnd(stationId: String) = "rental/station/$stationId/claim/end"
    fun stationSessionClear(stationId: String) = "rental/station/$stationId/session/clear"
    fun stationCredit(stationId: String) = "rental/station/$stationId/credit"
    fun stationCreditAck(stationId: String) = "rental/station/$stationId/credit/ack"
    fun stationAppInventory(stationId: String) = "rental/station/$stationId/apps"
    fun stationAdminOpen(stationId: String) = "rental/station/$stationId/admin/open"
    fun stationAppsReset(stationId: String) = "rental/station/$stationId/apps/reset"
    fun stationCommand(stationId: String) = "rental/station/$stationId/command"
    fun stationCommandAck(stationId: String) = "rental/station/$stationId/command/ack"
}
