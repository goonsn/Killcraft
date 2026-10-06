package dev.killcraft.mc;

import net.minecraft.core.BlockPos;
import net.minecraft.world.level.Level;
import net.minecraft.world.level.block.entity.BlockEntity;
import net.minecraft.world.level.block.state.BlockState;

/**
 * Tells Killcraft every second that this terminal is here (while a player is near): Killcraft keeps
 * an ULTRAKILL terminal for each one it keeps hearing about, so placed, broken, loaded and unloaded
 * terminals all come and go by themselves.
 */
public final class TerminalBlockEntity extends BlockEntity {
	public TerminalBlockEntity(BlockPos pos, BlockState state) {
		super(Killcraft.TERMINAL_ENTITY, pos, state);
	}

	static void serverTick(Level level, BlockPos pos, BlockState state, TerminalBlockEntity terminal) {
		if ((level.getGameTime() + pos.hashCode()) % 20 != 0
			|| level.getNearestPlayer(pos.getX() + 0.5, pos.getY() + 0.5, pos.getZ() + 0.5, 96.0, false) == null) {
			return;
		}
		int facing = state.getValue(TerminalBlock.FACING).get2DDataValue();
		Link.push(Killcraft.EV_TERMINAL, pos.getX(), pos.getY(), pos.getZ(), facing, 0);
	}
}
